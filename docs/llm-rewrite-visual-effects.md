# Visual Effects for LLM Rewrite Phase

## Motivation
When the decompiler finishes its own transformation passes, a preliminary version of the code is shown while the LLM produces a more readable rewrite. This wait can last up to a minute. A small green bar at the bottom is easy to overlook, leaving users unsure if more output is coming. We need to visually block the interim code and clearly communicate that a refined version is on the way.

## Proposed Visual Treatments

### 1. Dimmed Code With Shimmering Overlay
- Render the preliminary code at reduced opacity and apply a slight blur (`filter: blur(1px)`) to signal it is not final.
- Add an absolutely positioned overlay covering the code block. The overlay uses animated diagonal stripes to suggest ongoing work:
  ```css
  .rewrite-wait-cover {
      position: absolute;
      inset: 0;
      background: repeating-linear-gradient(
          135deg,
          rgba(255,255,255,0.15) 0 20px,
          rgba(255,255,255,0.05) 20px 40px);
      background-size: 200% 100%;
      animation: stripe-shift 1.2s linear infinite;
      display: flex;
      align-items: center;
      justify-content: center;
      color: #e0ffe0;
      font-weight: 600;
      text-shadow: 0 0 2px #000;
  }
  @keyframes stripe-shift {
      from { background-position: 0 0; }
      to   { background-position: 200% 0; }
  }
  ```
- Centered text reads "Refining code with AI…" to remove any ambiguity.
- Once the LLM response arrives, fade the overlay out (`opacity` transition) and remove the blur from the code container.

### 2. Rolling Line Skeletons
- Instead of dimming the entire block, replace each code line with a gray rectangle whose highlight sweeps horizontally (common "skeleton loader" effect).
- Implementation outline:
  ```css
  .code-skeleton-line {
      height: 1em;
      margin: 2px 0;
      background: linear-gradient(90deg,#2e2e2e 25%,#3e3e3e 37%,#2e2e2e 63%);
      background-size: 400% 100%;
      animation: skeleton-shimmer 1.6s ease-in-out infinite;
  }
  @keyframes skeleton-shimmer {
      0% { background-position: 100% 0; }
      100% { background-position: -100% 0; }
  }
  ```
- The preliminary code remains in memory but hidden. When the LLM output is ready, replace the skeleton lines with the final formatted code and stop the animation.

### 3. Curtain Sweep Reveal (optional addition)
- Keep the dimmed code visible but cover it with a translucent curtain. A vertical gradient slides from left to right, hinting that the window is being "rewritten".
- Use CSS `clip-path` or a moving mask to reveal the final code as the curtain retreats once the LLM response is available.

## Implementation Notes
- The overlay or skeleton container should be inserted only when the LLM call is pending. A simple flag in the rendering component can toggle the effect:
  ```javascript
  if (state.waitingForLlm) {
      showOverlay();
  } else {
      hideOverlay();
      renderFinalCode();
  }
  ```
- Ensure the overlay is positioned above line numbers and scroll bars so it completely covers interactive elements.
- Provide an accessible live region (`aria-live="polite"`) announcing "Generating refined code" for screen‑reader users.
- Keep CPU/GPU usage modest by pausing animations when the tab is hidden (`visibilitychange` event).

## Summary
Applying an animated overlay or skeleton loader makes it unmistakable that the displayed code is provisional. Users receive immediate feedback that an AI rewrite is in progress and the initial text is not the final output. Once the LLM response arrives, the overlay fades away, revealing the polished code without abrupt jumps.

