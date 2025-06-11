
🎬 Say Goodbye to Animator Chaos!

Tired of animations interrupting each other like impatient toddlers? 😤
Sick of writing spaghetti coroutines just to play two clips in a row? 🍝

AnimationController is here to save your sanity! 🦸‍♂️
It’s a plug-and-play Unity tool that makes playing, queuing, looping, locking, and fading animations feel like magic ✨—without the dark arts of Mecanim wizardry.

# 🎮 AnimationController for Unity

`AnimationController` is a powerful and flexible animation playback manager built on top of Unity's `Animator`. It provides easy-to-use APIs for controlling animations across multiple layers with support for:

✅ Crossfade playback  
✅ Looping and state chaining  
✅ Interrupts and layer locking  
✅ Returning to previous states  
✅ Completion callbacks  
✅ Cached parameter APIs for Blend Trees  
✅ Coroutine-safe fade-outs

> 💡 **Optimized for performance, robustness, and scalability in production games.**

---

## ✨ Features

- 🔁 Seamless animation chaining (queue-based)
- 🔒 Layer locking to prevent interruptions
- ⏱ Completion callbacks and automatic fade-outs
- ⚙️ Cached hash lookups for parameters and states
- 🎯 Fade and weight control for animation layers
- 🧵 Coroutine-based execution with clean handling on disable



## 📦 Installation

1. Copy the `AnimationController.cs` script into your Unity project's `Scripts` folder.
2. Attach it to any GameObject that has an `Animator` component.
3. Set up your Animator Controller with states and layers as needed.



## 🚀 Basic Usage

```csharp
public class CharacterController : MonoBehaviour
{
    private AnimationController _animCtrl;

    void Awake()
    {
        _animCtrl = GetComponent<AnimationController>();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            _animCtrl.Play("Jump", layer: 0, fadeDuration: 0.2f);
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            _animCtrl.Play("Run", loop: true);
        }
    }
}
````


## 🧠 Advanced Usage

### Queue animations with callbacks

```csharp
_animCtrl.Queue("DrawSword", onComplete: () =>
{
    Debug.Log("Sword drawn, now attacking!");
    _animCtrl.Queue("Attack");
});
```

### Locking layers until animation completes

```csharp
_animCtrl.Play("PowerUp", lockLayer: true, onComplete: () =>
{
    Debug.Log("Power-up done, now idle.");
    _animCtrl.Play("Idle");
});
```

### Returning to previous looping state after a non-looping one

```csharp
_animCtrl.Play("Run", loop: true);
_animCtrl.Queue("HitReact", returnToPrevious: true);
```

### Blend tree parameter control

```csharp
_animCtrl.SetFloat("Speed", 0.8f);
_animCtrl.SetBool("IsGrounded", true);
```

---

## 📘 Public API Reference

| Method                                                                                                                       | Description                                                                     |
| ---------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------- |
| `Play(string stateName, int layer, float fadeDuration, bool loop, bool returnToPrevious, bool lockLayer, Action onComplete)` | Plays an animation immediately. Interrupts the current animation if not locked. |
| `Queue(string stateName, ...)`                                                                                               | Queues an animation after the current one finishes.                             |
| `InterruptLayer(int layer, float fadeOutDuration, bool force)`                                                               | Interrupts a layer, clearing its queue and optionally fading out its weight.    |
| `IsLayerLocked(int layer)`                                                                                                   | Returns true if the specified layer is currently locked.                        |
| `SetFloat(string param, float value)`                                                                                        | Sets a float parameter (cached).                                                |
| `SetBool(string param, bool value)`                                                                                          | Sets a bool parameter (cached).                                                 |
| `SetInt(string param, int value)`                                                                                            | Sets an int parameter (cached).                                                 |
| `SetTrigger(string param)`                                                                                                   | Sets a trigger parameter (cached).                                              |

---

## ✅ Best Practices

* Use **looping + returnToPrevious** for reactive states like hit-reactions or dodges.
* Use **locking** for non-interruptible animations like special moves or transformations.
* Use **fadeOutDuration** in `InterruptLayer()` to avoid harsh cut-offs.
* Avoid mixing too many **layer weights** without proper fading to prevent blending artifacts.
* Always provide a **default looping state** to fall back to (like Idle or Run).

---

## 🔧 Requirements

* Unity 2020.3 or later
* Animator component with states configured

---

## 🧙‍♂️ License

Licensed under the Apache License, Version 2.0 (the "License");  
you may not use this file except in compliance with the License.  
You may obtain a copy of the License at:

[http://www.apache.org/licenses/LICENSE-2.0](http://www.apache.org/licenses/LICENSE-2.0)

Unless required by applicable law or agreed to in writing, software  
distributed under the License is distributed on an "AS IS" BASIS,  
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.  
See the License for the specific language governing permissions and  
limitations under the License.

---

## ❤️ Credits

Developed by Amirhossein Jafari Barani
Feel free to contribute, report bugs, or suggest improvements!
Let me know if you'd like this adapted into a `README.md` file directly or added to a Unity `.unitypackage` template.
