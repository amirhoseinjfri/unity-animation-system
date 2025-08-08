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

    private void Awake()
    {
        _animator = GetComponent<Animator>();
    }

    private void OnDisable()
    {
        foreach (var kv in _layerPlaybackCoroutines)
            if (kv.Value != null) StopCoroutine(kv.Value);
        foreach (var kv in _layerFadeOutCoroutines)
            if (kv.Value != null) StopCoroutine(kv.Value);

        _layerPlaybackCoroutines.Clear();
        _layerFadeOutCoroutines.Clear();
        _layerQueues.Clear();
        _lastLoopedStateByLayer.Clear();
        _lockedLayers.Clear();
    }

#if UNITY_EDITOR
    private void OnDestroy()
    {
        StateHashCache.Clear();
        ParamHashCache.Clear();
    }
#endif

    public bool IsLayerLocked(int layer) => _lockedLayers.Contains(layer);
    public bool IsAnyLayerPlaying => _layerPlaybackCoroutines.Count > 0;
    public bool IsLayerPlaying(int layer) => _layerPlaybackCoroutines.ContainsKey(layer);

    public void Play(string stateName, int layer = 0, float fadeDuration = 0.1f,
                         bool loop = false, bool returnToPrevious = false,
                         bool lockLayer = false, Action onComplete = null)
    {
        if (!IsValidLayer(layer) || !IsValidForExecution()) return;
        if (IsLayerLocked(layer)) return;

        StopLayerCoroutine(_layerPlaybackCoroutines, layer);
        GetOrCreateQueue(layer).Clear();
        Queue(stateName, layer, fadeDuration, loop, returnToPrevious, lockLayer, onComplete);
    }

    public void Queue(string stateName, int layer = 0, float fadeDuration = 0.1f,
                         bool loop = false, bool returnToPrevious = false,
                         bool lockLayer = false, Action onComplete = null)
    {
        if (!IsValidLayer(layer) || !IsValidForExecution()) return;

        var request = CreateRequest(stateName, fadeDuration, loop, returnToPrevious, lockLayer, onComplete);

        if (loop)
        {
            _lastLoopedStateByLayer[layer] = CreateRequest(stateName, defaultLoopFadeInDuration, true, false, false, null);
        }

        GetOrCreateQueue(layer).AddLast(request);
        StartNext(layer);
    }

    public void InterruptLayer(int layer, float fadeOutDuration = 0.2f, bool force = false)
    {
        if (!IsValidLayer(layer) || !IsValidForExecution()) return;
        if (_lockedLayers.Contains(layer) && !force) return;
        if (force) _lockedLayers.Remove(layer);

        StopLayerCoroutine(_layerPlaybackCoroutines, layer);
        StopLayerCoroutine(_layerFadeOutCoroutines, layer);

        if (_layerQueues.TryGetValue(layer, out var queue))
            queue.Clear();

        _lastLoopedStateByLayer.Remove(layer);

        if (fadeOutDuration > 0 && _animator.GetLayerWeight(layer) > 0)
            _layerFadeOutCoroutines[layer] = StartCoroutine(FadeOutLayerRoutine(layer, fadeOutDuration));
        else
            SetLayerWeightSafe(layer, 0f);
    }

    public void SetFloat(string param, float value)
    {
        if (IsValidForExecution())
            _animator.SetFloat(GetParamHash(param), value);
    }

    public void SetBool(string param, bool value)
    {
        if (IsValidForExecution())
            _animator.SetBool(GetParamHash(param), value);
    }

    public void SetInt(string param, int value)
    {
        if (IsValidForExecution())
            _animator.SetInteger(GetParamHash(param), value);
    }

    public void SetTrigger(string param)
    {
        if (IsValidForExecution())
            _animator.SetTrigger(GetParamHash(param));
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (!_animator.HasState(layer, req.StateHash))
        {
            Debug.LogError($"State '{req.StateName}' not found on layer {layer}.", this);
            _lockedLayers.Remove(layer);
            _layerPlaybackCoroutines.Remove(layer);
            StartNext(layer);
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
        {
            _lockedLayers.Remove(layer);
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
            _lockedLayers.Remove(layer);
            _layerPlaybackCoroutines.Remove(layer);
            StartNext(layer);
            yield break;
        }

        float timer = 0f;
        while (timer < clipLength - req.FadeDuration)
        {
            if (!IsValidForExecution())
            {
                _lockedLayers.Remove(layer);
                yield break;
            }

            if (!_animator.IsInTransition(layer) &&
                _animator.GetCurrentAnimatorStateInfo(layer).shortNameHash != req.StateHash)
            {
                _lockedLayers.Remove(layer);
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
            _layerFadeOutCoroutines[layer] = StartCoroutine(FadeOutLayerRoutine(layer, 0.2f));
        }

        _lockedLayers.Remove(layer);
        _layerPlaybackCoroutines.Remove(layer);
        StartNext(layer);
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

            var info = _animator.GetCurrentAnimatorStateInfo(layer);
            if (!_animator.IsInTransition(layer) && info.shortNameHash == targetStateHash)
            {
                var clips = _animator.GetCurrentAnimatorClipInfo(layer, _clipInfoBuffer);
                if (clips > 0)
                {
                    callback(_clipInfoBuffer[0].clip.length);
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

    private bool IsValidForExecution() => isActiveAndEnabled && _animator != null && _animator.enabled;

    private bool IsValidLayer(int layer)
    {
        if (layer < 0 || _animator == null || layer >= _animator.layerCount)
        {
            Debug.LogWarning($"Invalid layer index {layer}.", this);
            return false;
        }
        return true;
    }

    private void SetLayerWeightSafe(int layer, float weight)
    {
        if (IsValidForExecution())
            _animator.SetLayerWeight(layer, weight);
    }

    private void CrossFadeSafe(int stateHash, float fadeDuration, int layer)
    {
        if (IsValidForExecution())
            _animator.CrossFadeInFixedTime(stateHash, fadeDuration, layer, 0f);
    }

    private void StopLayerCoroutine(Dictionary<int, Coroutine> dict, int layer)
    {
        if (dict.TryGetValue(layer, out var coro))
        {
            if (coro != null) StopCoroutine(coro);
            dict.Remove(layer);
        }
    }

    private LinkedList<AnimationRequest> GetOrCreateQueue(int layer)
    {
        if (!_layerQueues.TryGetValue(layer, out var queue))
            _layerQueues[layer] = queue = new LinkedList<AnimationRequest>();
        return queue;
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

