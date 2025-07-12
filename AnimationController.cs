using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages Animator crossfade‐based playback across multiple layers with support for looping,
/// chaining, interrupts, return‐to‐previous, and completion callbacks per‐layer.
/// Also provides high‐performance, cached parameter APIs for driving Blend Trees,
/// and supports “locking” layers to prevent interruptions until the current animation completes.
/// This production‐ready version is optimized for performance, memory, and robustness.
/// </summary>
[RequireComponent(typeof(Animator))]
public class AnimationController : MonoBehaviour
{
    private Animator _animator;

    [Tooltip("Default fade duration used when queuing a new looping animation that might be returned to later.")]
    [SerializeField] private float defaultLoopFadeInDuration = 0.2f;

    private static readonly Dictionary<string, int> StateHashCache = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> ParamHashCache = new Dictionary<string, int>(StringComparer.Ordinal);

    private class AnimationRequest
    {
        public int StateHash;
        public string StateName;
        public bool Loop;
        public bool ReturnToPrevious;
        public float FadeDuration;
        public bool LockLayer;
        public Action OnComplete;
    }

    private readonly Dictionary<int, LinkedList<AnimationRequest>> _layerQueues = new Dictionary<int, LinkedList<AnimationRequest>>();
    private readonly Dictionary<int, Coroutine> _layerPlaybackCoroutines = new Dictionary<int, Coroutine>();
    private readonly Dictionary<int, Coroutine> _layerFadeOutCoroutines = new Dictionary<int, Coroutine>();
    private readonly Dictionary<int, AnimationRequest> _lastLoopedStateByLayer = new Dictionary<int, AnimationRequest>();
    private readonly HashSet<int> _lockedLayers = new HashSet<int>();

    private void Awake()
    {
        _animator = GetComponent<Animator>();
    }

    private void OnDisable()
    {
        // Stop all coroutines managed by this system to prevent them from running while disabled.
        foreach (var coroutine in _layerPlaybackCoroutines.Values)
        {
            if (coroutine != null) StopCoroutine(coroutine);
        }
        foreach (var coroutine in _layerFadeOutCoroutines.Values)
        {
            if (coroutine != null) StopCoroutine(coroutine);
        }

        _layerPlaybackCoroutines.Clear();
        _layerFadeOutCoroutines.Clear();
        _layerQueues.Clear();
        _lastLoopedStateByLayer.Clear();
        _lockedLayers.Clear();
    }

    /// <summary>Is the given layer currently locked?</summary>
    public bool IsLayerLocked(int layer) => _lockedLayers.Contains(layer);

    /// <summary>
    /// Plays an animation immediately, interrupting any non‐locked animations on the same layer.
    /// If the layer is locked, the request is ignored.
    /// </summary>
    public void Play(string stateName, int layer = 0, float fadeDuration = 0.1f,
                     bool loop = false, bool returnToPrevious = false,
                     bool lockLayer = false, Action onComplete = null)
    {
        if (IsLayerLocked(layer))
            return;

        if (_layerPlaybackCoroutines.TryGetValue(layer, out var coro))
        {
            if (coro != null) StopCoroutine(coro);
            _layerPlaybackCoroutines.Remove(layer);
        }

        GetOrCreateQueue(layer).Clear();

        Queue(stateName, layer, fadeDuration, loop, returnToPrevious, lockLayer, onComplete);
    }

    /// <summary>Queues an animation to run after the current one finishes on that layer.</summary>
    public void Queue(string stateName, int layer = 0, float fadeDuration = 0.1f,
                      bool loop = false, bool returnToPrevious = false,
                      bool lockLayer = false, Action onComplete = null)
    {
        var request = CreateRequest(stateName, fadeDuration, loop, returnToPrevious, lockLayer, onComplete);

        if (loop)
            _lastLoopedStateByLayer[layer] = CreateRequest(stateName, defaultLoopFadeInDuration, true, false, false, null);

        GetOrCreateQueue(layer).AddLast(request);
        StartNext(layer);
    }

    /// <summary>
    /// Interrupts the specified layer (unless locked, or if force=true), clears its queue,
    /// and optionally fades out its weight.
    /// </summary>
    public void InterruptLayer(int layer, float fadeOutDuration = 0.2f, bool force = false)
    {
        if (_lockedLayers.Contains(layer) && !force) return;
        if (force) _lockedLayers.Remove(layer);

        if (_layerPlaybackCoroutines.TryGetValue(layer, out var coro))
        {
            if (coro != null) StopCoroutine(coro);
            _layerPlaybackCoroutines.Remove(layer);
        }

        GetOrCreateQueue(layer)?.Clear();
        _lastLoopedStateByLayer.Remove(layer);

        if (_layerFadeOutCoroutines.TryGetValue(layer, out var fadeCoro))
            StopCoroutine(fadeCoro);

        if (gameObject.activeInHierarchy && fadeOutDuration > 0 && _animator.GetLayerWeight(layer) > 0)
            _layerFadeOutCoroutines[layer] = StartCoroutine(FadeOutLayerRoutine(layer, fadeOutDuration));
        else if (fadeOutDuration <= 0)
            _animator.SetLayerWeight(layer, 0f);
    }

    public void SetFloat(string param, float value) => _animator.SetFloat(GetParamHash(param), value);
    public void SetBool(string param, bool value) => _animator.SetBool(GetParamHash(param), value);
    public void SetInt(string param, int value) => _animator.SetInteger(GetParamHash(param), value);
    public void SetTrigger(string param) => _animator.SetTrigger(GetParamHash(param));

    private void StartNext(int layer)
    {
        if (_layerPlaybackCoroutines.ContainsKey(layer)) return;
        var queue = GetOrCreateQueue(layer);
        if (queue.Count == 0) return;

        var req = queue.First.Value;
        queue.RemoveFirst();
        _layerPlaybackCoroutines[layer] = StartCoroutine(PlayRoutine(req, layer));
    }

    private IEnumerator PlayRoutine(AnimationRequest req, int layer)
    {
        if (req.LockLayer) _lockedLayers.Add(layer);

        if (_layerFadeOutCoroutines.TryGetValue(layer, out var fadeCoro))
        {
            if (fadeCoro != null) StopCoroutine(fadeCoro);
            _layerFadeOutCoroutines.Remove(layer);
        }

        _animator.SetLayerWeight(layer, 1f);
        _animator.CrossFadeInFixedTime(req.StateHash, req.FadeDuration, layer, 0f);
        if (req.FadeDuration > 0)
            yield return new WaitForSeconds(req.FadeDuration);

        if (!isActiveAndEnabled)
        {
            yield break;
        }

        if (req.Loop)
        {
            req.OnComplete?.Invoke();
            _layerPlaybackCoroutines.Remove(layer);
            StartNext(layer);
            yield break;
        }

        float clipLength = 0f;
        yield return StartCoroutine(GetClipLengthRoutine(layer, req.StateHash, len => clipLength = len));

        if (clipLength <= 0f)
        {
            Debug.LogWarning($"Could not determine clip length for '{req.StateName}' on layer {layer}. Treating as looping state.");
            _layerPlaybackCoroutines.Remove(layer);
            StartNext(layer);
            yield break;
        }

        float timer = 0f;
        while (timer < clipLength - req.FadeDuration)
        {
            if (!isActiveAndEnabled)
            {
                if (req.LockLayer) _lockedLayers.Remove(layer);
                yield break;
            }

            if (!_animator.IsInTransition(layer) &&
                            _animator.GetCurrentAnimatorStateInfo(layer).shortNameHash != req.StateHash)
            {
                if (req.LockLayer) _lockedLayers.Remove(layer);
                _layerPlaybackCoroutines.Remove(layer);
                StartNext(layer);
                yield break;
            }
            timer += Time.deltaTime;
            yield return null;
        }

        req.OnComplete?.Invoke();

        if (req.ReturnToPrevious && _lastLoopedStateByLayer.TryGetValue(layer, out var lastReq))
        {
            GetOrCreateQueue(layer).AddFirst(lastReq);
        }
        else if (GetOrCreateQueue(layer).Count == 0)
        {
            if (this.isActiveAndEnabled)
            {
                _layerFadeOutCoroutines[layer] = StartCoroutine(FadeOutLayerRoutine(layer, 0.2f));
            }
        }

        if (req.LockLayer) _lockedLayers.Remove(layer);

        _layerPlaybackCoroutines.Remove(layer);
        StartNext(layer);
    }

    /// <summary>
    /// Waits until the animator has transitioned into the target state, then returns its clip length.
    /// Note: This is most reliable for states with a single clip and may be inaccurate for Blend Trees.
    /// </summary>
    private IEnumerator GetClipLengthRoutine(int layer, int targetStateHash, Action<float> callback)
    {
        const float TIMEOUT = 1f;
        float elapsed = 0f;

        while (elapsed < TIMEOUT)
        {
            var info = _animator.GetCurrentAnimatorStateInfo(layer);
            if (!_animator.IsInTransition(layer) && info.shortNameHash == targetStateHash)
            {
                var clips = _animator.GetCurrentAnimatorClipInfo(layer);
                if (clips.Length > 0)
                {
                    callback(clips[0].clip.length);
                    yield break;
                }
            }
            elapsed += Time.deltaTime;
            yield return null;
        }

        callback(0f);
    }



    private IEnumerator FadeOutLayerRoutine(int layer, float duration)
    {
        float start = _animator.GetLayerWeight(layer), t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            _animator.SetLayerWeight(layer, Mathf.Lerp(start, 0f, t / duration));
            yield return null;
        }
        _animator.SetLayerWeight(layer, 0f);
        _layerFadeOutCoroutines.Remove(layer);
    }

    private static AnimationRequest CreateRequest(string stateName, float fadeDuration, bool loop,
                                                  bool returnToPrevious, bool lockLayer, Action onComplete)
    {
        if (string.IsNullOrEmpty(stateName))
            throw new ArgumentException("Animation stateName cannot be null or empty.", nameof(stateName));

        return new AnimationRequest
        {
            StateHash = GetStateHash(stateName),
            StateName = stateName,
            FadeDuration = fadeDuration,
            Loop = loop,
            ReturnToPrevious = returnToPrevious,
            LockLayer = lockLayer,
            OnComplete = onComplete
        };
    }

    private LinkedList<AnimationRequest> GetOrCreateQueue(int layer)
    {
        if (!_layerQueues.TryGetValue(layer, out var queue))
            _layerQueues[layer] = queue = new LinkedList<AnimationRequest>();
        return queue;
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
