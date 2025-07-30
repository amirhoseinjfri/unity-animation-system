# 🎬 AnimationController for Unity

A production-ready animation manager that makes Unity's Animator actually pleasant to work with.

## ✨ Features

- **Queue-based animation chaining** - Play animations in sequence with callbacks
- **Layer locking** - Prevent interruptions during critical animations  
- **Smart looping** - Return to previous states after temporary animations
- **Performance optimized** - Cached hash lookups and coroutine management
- **Bulletproof safety** - Handles component lifecycle edge cases

## 🚀 Quick Start

```csharp
// Basic playback
_animCtrl.Play("Jump", fadeDuration: 0.2f);

// Queue with callback
_animCtrl.Queue("Attack", onComplete: () => Debug.Log("Attack finished!"));

// Lock layer during critical animation
_animCtrl.Play("PowerUp", lockLayer: true);

// Loop with return-to-previous
_animCtrl.Play("Run", loop: true);
_animCtrl.Queue("HitReact", returnToPrevious: true); // Returns to Run after
```

## 📋 API Reference

| Method | Description | Parameters |
|--------|-------------|------------|
| `Play()` | Play animation immediately, interrupting current | `stateName` (exact string name from Animator), `layer=0, fadeDuration=0.1f, loop=false, returnToPrevious=false, lockLayer=false, onComplete=null` |
| `Queue()` | Queue animation to play after current finishes | `stateName` (exact string name from Animator), `layer=0, fadeDuration=0.1f, loop=false, returnToPrevious=false, lockLayer=false, onComplete=null` |
| `InterruptLayer()` | Stop layer and clear queue | `layer, fadeOutDuration=0.2f, force=false` |
| `IsLayerLocked()` | Check if layer is locked | `layer` |
| `SetFloat/Bool/Int/Trigger()` | Cached parameter setters | `param, value` |
| `IsAnyLayerPlaying()` | Check if any layer is playing | `layer` |
| `IsLayerPlaying()` | Check if layer is playing | `layer` |

## 📦 Installation

1. Add `AnimationController.cs` to your project
2. Attach to GameObject with `Animator` component
3. Configure your Animator Controller states
4. **Important:** Enable "Loop Time" checkbox on animation clips you want to loop

## 🔧 Requirements

- Unity 2020.3+
- Animator component

## 📄 License

Apache License 2.0

---

*Made with ❤️ by Amirhossein Jafari Barani*
