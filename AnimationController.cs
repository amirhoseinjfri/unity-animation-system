using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages Animator crossfade‐based playback across multiple layers with support for looping,
/// chaining, interrupts, return‐to‐previous, and completion callbacks per‐layer.
/// Also provides high‐performance, cached parameter APIs for driving Blend Trees,
/// and supports "locking" layers to prevent interruptions until the current animation completes.
/// </summary>
[RequireComponent(typeof(Animator))]
public class AnimationController : MonoBehaviour
{
    private Animator _animator;

    [Tooltip("Default fade duration used when queuing a new looping animation that might be returned to later.")]
    [SerializeField] private float defaultLoopFadeInDuration = 0.2f;

    [Tooltip("Default fade-in duration for layer weights.")]
    [SerializeField] private float defaultLayerFadeInDuration = 0.1f;

    private static readonly Dictionary<string, int> StateHashCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> ParamHashCache = new(StringComparer.Ordinal);

    private readonly Dictionary<int, LinkedList<AnimationRequest>> _layerQueues = new();
    private readonly Dictionary<int, Coroutine> _layerPlaybackCoroutines = new();
    private readonly Dictionary<int, Coroutine> _layerFadeOutCoroutines = new();
    private readonly Dictionary<int, AnimationRequest> _lastLoopedStateByLayer = new();
    private readonly HashSet<int> _lockedLayers = new();

    private AnimatorClipInfo[] _clipInfoBuffer = new AnimatorClipInfo[1];

    private static readonly Stack<AnimationRequest> RequestPool = new Stack<AnimationRequest>();

    private class AnimationRequest
    {
        public int StateHash;
        public string StateName;
        public bool Loop;
        public bool ReturnToPrevious;
        public float FadeDuration;
        public bool LockLayer;
        public Action OnComplete;

        public void Reset()
        {
            StateHash = 0;
            StateName = null;
            Loop = false;
            ReturnToPrevious = false;
            FadeDuration = 0f;
            LockLayer = false;
            OnComplete = null;
        }
    }

    private void Awake()
    {
        _animator = GetComponent<Animator>();
    }

    private void OnDisable()
    {
        StopAllCoroutines();

        _layerPlaybackCoroutines.Clear();
        _layerFadeOutCoroutines.Clear();
        _lockedLayers.Clear();

        foreach (var queue in _layerQueues.Values)
        {
            foreach (var request in queue)
            {
                ReleaseRequest(request);
            }
            queue.Clear();
        }
        _layerQueues.Clear();

        foreach (var request in _lastLoopedStateByLayer.Values)
        {
            ReleaseRequest(request);
        }
        _lastLoopedStateByLayer.Clear();
    }

#if UNITY_EDITOR
    private void OnDestroy()
    {
        StateHashCache.Clear();
        ParamHashCache.Clear();
    }
#endif

    /// <summary>
    /// Checks if a layer is currently locked, preventing new animations from playing until the current one completes.
    /// </summary>
    /// <param name="layer">The animator layer index to check.</param>
    /// <returns>True if the layer is locked, otherwise false.</returns>
    public bool IsLayerLocked(int layer) => _lockedLayers.Contains(layer);

    /// <summary>
    /// Returns true if any animation is currently playing on any layer.
    /// </summary>
    public bool IsAnyLayerPlaying => _layerPlaybackCoroutines.Count > 0;

    /// <summary>
    /// Checks if an animation is currently playing on a specific layer.
    /// </summary>
    /// <param name="layer">The animator layer index to check.</param>
    /// <returns>True if an animation is active on the layer, otherwise false.</returns>
    public bool IsLayerPlaying(int layer) => _layerPlaybackCoroutines.ContainsKey(layer);

    /// <summary>
    /// Immediately interrupts a layer, clears its queue, and plays a new animation.
    /// </summary>
    /// <param name="stateName">The exact name of the animation state in the Animator Controller.</param>
    /// <param name="layer">The animator layer index to play on.</param>
    /// <param name="fadeDuration">The duration of the cross-fade in seconds.</param>
    /// <param name="loop">If true, the animation will loop indefinitely and the queue will proceed immediately.</param>
    /// <param name="returnToPrevious">If true, the last known looping animation will be re-queued after this one completes.</param>
    /// <param name="lockLayer">If true, the layer will be locked from interruptions until this animation completes.</param>
    /// <param name="onComplete">An action to be invoked when the animation finishes playing.</param>
    public void Play(string stateName, int layer = 0, float fadeDuration = 0.1f,
                     bool loop = false, bool returnToPrevious = false,
                     bool lockLayer = false, Action onComplete = null)
    {
        if (!IsValidLayer(layer) || !IsValidForExecution()) return;
        if (IsLayerLocked(layer)) return;

        StopLayerCoroutine(_layerPlaybackCoroutines, layer);
        ClearQueueAndReleaseRequests(layer);
        
        Queue(stateName, layer, fadeDuration, loop, returnToPrevious, lockLayer, onComplete);
    }

    /// <summary>
    /// Adds a new animation to the end of a layer's queue. It will play after all previously queued animations are finished.
    /// </summary>
    /// <param name="stateName">The exact name of the animation state in the Animator Controller.</param>
    /// <param name="layer">The animator layer index to play on.</param>
    /// <param name="fadeDuration">The duration of the cross-fade in seconds.</param>
    /// <param name="loop">If true, the animation will loop indefinitely and the queue will proceed immediately.</param>
    /// <param name="returnToPrevious">If true, the last known looping animation will be re-queued after this one completes.</param>
    /// <param name="lockLayer">If true, the layer will be locked from interruptions until this animation completes.</param>
    /// <param name="onComplete">An action to be invoked when the animation finishes playing.</param>
    public void Queue(string stateName, int layer = 0, float fadeDuration = 0.1f,
                    bool loop = false, bool returnToPrevious = false,
                    bool lockLayer = false, Action onComplete = null)
    {
        if (!IsValidLayer(layer) || !IsValidForExecution()) return;

        var request = CreateRequest(stateName, fadeDuration, loop, returnToPrevious, lockLayer, onComplete);

        if (loop)
        {
            if (_lastLoopedStateByLayer.TryGetValue(layer, out var oldLoopedRequest))
            {
                ReleaseRequest(oldLoopedRequest);
            }
            _lastLoopedStateByLayer[layer] = CreateRequest(stateName, defaultLoopFadeInDuration, true, false, false, null);
        }

        GetOrCreateQueue(layer).AddLast(request);
        StartNext(layer);
    }

    /// <summary>
    /// Stops all current and queued animations on a layer and fades its weight to zero.
    /// </summary>
    /// <param name="layer">The animator layer index to interrupt.</param>
    /// <param name="fadeOutDuration">The time in seconds for the layer's weight to fade to zero.</param>
    /// <param name="force">If true, this will interrupt a locked layer.</param>
    public void InterruptLayer(int layer, float fadeOutDuration = 0.2f, bool force = false)
    {
        if (!IsValidLayer(layer) || !IsValidForExecution()) return;
        if (_lockedLayers.Contains(layer) && !force) return;
        
        if (force) _lockedLayers.Remove(layer);

        StopLayerCoroutine(_layerPlaybackCoroutines, layer);
        StopLayerCoroutine(_layerFadeOutCoroutines, layer);

        ClearQueueAndReleaseRequests(layer);

        if (_lastLoopedStateByLayer.TryGetValue(layer, out var loopedRequest))
        {
            ReleaseRequest(loopedRequest);
            _lastLoopedStateByLayer.Remove(layer);
        }
        
        if (fadeOutDuration > 0 && _animator.GetLayerWeight(layer) > 0)
            _layerFadeOutCoroutines[layer] = StartCoroutine(FadeOutLayerRoutine(layer, fadeOutDuration));
        else
            SetLayerWeightSafe(layer, 0f);
    }

    /// <summary>
    /// Sets a float parameter on the Animator, using a hash cache.
    /// </summary>
    /// <param name="param">The name of the parameter.</param>
    /// <param name="value">The value to set.</param>
    public void SetFloat(string param, float value)
    {
        if (IsValidForExecution()) _animator.SetFloat(GetParamHash(param), value);
    }

    /// <summary>
    /// Sets a boolean parameter on the Animator, using a hash cache.
    /// </summary>
    /// <param name="param">The name of the parameter.</param>
    /// <param name="value">The value to set.</param>
    public void SetBool(string param, bool value)
    {
        if (IsValidForExecution()) _animator.SetBool(GetParamHash(param), value);
    }

    /// <summary>
    /// Sets an integer parameter on the Animator, using a hash cache.
    /// </summary>
    /// <param name="param">The name of the parameter.</param>
    /// <param name="value">The value to set.</param>
    public void SetInt(string param, int value)
    {
        if (IsValidForExecution()) _animator.SetInteger(GetParamHash(param), value);
    }

    /// <summary>
    /// Sets a trigger on the Animator, using a hash cache.
    /// </summary>
    /// <param name="param">The name of the trigger parameter.</param>
    public void SetTrigger(string param)
    {
        if (IsValidForExecution()) _animator.SetTrigger(GetParamHash(param));
    }

    private void StartNext(int layer)
    {
        if (!IsValidForExecution() || _layerPlaybackCoroutines.ContainsKey(layer)) return;

        var queue = GetOrCreateQueue(layer);
        if (queue.Count == 0) return;

        var req = queue.First.Value;
        queue.RemoveFirst();

        _layerPlaybackCoroutines[layer] = StartCoroutine(PlayRoutine(req, layer));
    }

    private IEnumerator PlayRoutine(AnimationRequest req, int layer)
    {
        try
        {
    #if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_animator.HasState(layer, req.StateHash))
            {
                Debug.LogError($"State '{req.StateName}' not found on layer {layer}.", this);
                yield break;
            }
    #endif
            if (req.LockLayer) _lockedLayers.Add(layer);
            StopLayerCoroutine(_layerFadeOutCoroutines, layer);

            yield return FadeInLayerRoutine(layer, defaultLayerFadeInDuration);
            CrossFadeSafe(req.StateHash, req.FadeDuration, layer);

            if (req.FadeDuration > 0)
                yield return new WaitForSeconds(req.FadeDuration);

            if (!IsValidForExecution())
                yield break;

            if (req.Loop)
            {
                req.OnComplete?.Invoke();
                yield break; 
            }

            float clipLength = 0f;
            yield return StartCoroutine(GetClipLengthRoutine(layer, req.StateHash, len => clipLength = len));

            if (clipLength <= 0f)
                yield break;

            float waitTime = clipLength - req.FadeDuration;
            if (waitTime > 0)
            {
                yield return new WaitForSeconds(waitTime);
            }

            if (!IsValidForExecution())
                yield break;
            
            req.OnComplete?.Invoke();

            if (req.ReturnToPrevious && _lastLoopedStateByLayer.TryGetValue(layer, out var lastReq))
            {
                var returnRequest = CreateRequest(lastReq.StateName, lastReq.FadeDuration, lastReq.Loop, false, false, null);
                GetOrCreateQueue(layer).AddFirst(returnRequest);
            }
            else if (GetOrCreateQueue(layer).Count == 0)
            {
                _layerFadeOutCoroutines[layer] = StartCoroutine(FadeOutLayerRoutine(layer, 0.2f));
            }
        }
        finally
        {
            _lockedLayers.Remove(layer);
            _layerPlaybackCoroutines.Remove(layer);
            ReleaseRequest(req);
            StartNext(layer);
        }
    }

    private IEnumerator GetClipLengthRoutine(int layer, int targetStateHash, Action<float> callback)
    {
        const float TIMEOUT = 1f;
        float elapsed = 0f;

        yield return null;

        while (elapsed < TIMEOUT)
        {
            if (!IsValidForExecution())
            {
                callback(0f);
                yield break;
            }

            var stateInfo = _animator.GetCurrentAnimatorStateInfo(layer);
            if (stateInfo.shortNameHash == targetStateHash && !_animator.IsInTransition(layer))
            {
                var clips = _animator.GetCurrentAnimatorClipInfo(layer, _clipInfoBuffer);
                if (clips > 0 && _clipInfoBuffer[0].clip != null)
                {
                    callback(_clipInfoBuffer[0].clip.length / stateInfo.speed);
                    yield break;
                }
            }
            elapsed += Time.deltaTime;
            yield return null;
        }
        
        Debug.LogWarning($"Could not get clip length for state hash {targetStateHash} on layer {layer}. Timeout reached.");
        callback(0f);
    }

    private IEnumerator FadeOutLayerRoutine(int layer, float duration)
    {
        if (!IsValidForExecution())
        {
            _layerFadeOutCoroutines.Remove(layer);
            yield break;
        }

        float start = _animator.GetLayerWeight(layer);
        float t = 0f;

        while (t < duration)
        {
            if (!IsValidForExecution())
            {
                _layerFadeOutCoroutines.Remove(layer);
                yield break;
            }
            t += Time.deltaTime;
            SetLayerWeightSafe(layer, Mathf.Lerp(start, 0f, t / duration));
            yield return null;
        }

        SetLayerWeightSafe(layer, 0f);
        _layerFadeOutCoroutines.Remove(layer);
    }
    
    private IEnumerator FadeInLayerRoutine(int layer, float duration)
    {
        float start = _animator.GetLayerWeight(layer);
        if (Mathf.Approximately(start, 1f)) yield break;
        
        float t = 0f;
        while (t < duration)
        {
            if (!IsValidForExecution()) yield break;
            t += Time.deltaTime;
            SetLayerWeightSafe(layer, Mathf.Lerp(start, 1f, t / duration));
            yield return null;
        }
        SetLayerWeightSafe(layer, 1f);
    }

    private bool IsValidForExecution() => this != null && isActiveAndEnabled && _animator != null && _animator.enabled && _animator.runtimeAnimatorController != null;
    
    private bool IsValidLayer(int layer)
    {
        if (_animator == null || layer < 0 || layer >= _animator.layerCount)
        {
            Debug.LogWarning($"Invalid layer index {layer}.", this);
            return false;
        }
        return true;
    }
    
    private void StopLayerCoroutine(Dictionary<int, Coroutine> dict, int layer)
    {
        if (dict.TryGetValue(layer, out var coro) && coro != null)
        {
            StopCoroutine(coro);
        }
        dict.Remove(layer);
    }
    
    private void ClearQueueAndReleaseRequests(int layer)
    {
        if (_layerQueues.TryGetValue(layer, out var queue))
        {
            foreach (var req in queue)
            {
                ReleaseRequest(req);
            }
            queue.Clear();
        }
    }

    private LinkedList<AnimationRequest> GetOrCreateQueue(int layer)
    {
        if (!_layerQueues.TryGetValue(layer, out var queue))
        {
            _layerQueues[layer] = queue = new LinkedList<AnimationRequest>();
        }
        return queue;
    }
    
    private void SetLayerWeightSafe(int layer, float weight)
    {
        if (IsValidForExecution()) _animator.SetLayerWeight(layer, weight);
    }

    private void CrossFadeSafe(int stateHash, float fadeDuration, int layer)
    {
        if (IsValidForExecution()) _animator.CrossFadeInFixedTime(stateHash, fadeDuration, layer, 0f);
    }


    private static AnimationRequest CreateRequest(string stateName, float fadeDuration, bool loop,
                                                  bool returnToPrevious, bool lockLayer, Action onComplete)
    {
        if (string.IsNullOrEmpty(stateName))
            throw new ArgumentException("Animation stateName cannot be null or empty.", nameof(stateName));

        var req = RequestPool.Count > 0 ? RequestPool.Pop() : new AnimationRequest();
        
        req.StateHash = GetStateHash(stateName);
        req.StateName = stateName;
        req.FadeDuration = fadeDuration;
        req.Loop = loop;
        req.ReturnToPrevious = returnToPrevious;
        req.LockLayer = lockLayer;
        req.OnComplete = onComplete;
        
        return req;
    }
    
    private static void ReleaseRequest(AnimationRequest request)
    {
        if (request == null) return;
        request.Reset();
        RequestPool.Push(request);
    }
    
    private static int GetStateHash(string name)
    {
        if (!StateHashCache.TryGetValue(name, out var h))
            StateHashCache[name] = h = Animator.StringToHash(name);
        return h;
    }

    private static int GetParamHash(string name)
    {
        if (!ParamHashCache.TryGetValue(name, out var h))
            ParamHashCache[name] = h = Animator.StringToHash(name);
        return h;
    }
}
