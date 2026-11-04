<script setup lang="ts">
// The cube mark (same geometry as the favicon). Its colour is an instrument, not a logo:
// emerald faces on a declared development server, amber on anything else — the same rule
// as ProductionActionGuard, made visible — and outlined while disconnected.
defineProps<{ state: 'cube' | 'prod' | 'off'; size?: number }>()
</script>

<template>
  <svg
    class="scope-mark"
    :class="state"
    viewBox="0 0 32 32"
    :width="size ?? 18"
    :height="size ?? 18"
    aria-hidden="true"
  >
    <g stroke-width="0.8" stroke-linejoin="round">
      <path class="face top" d="M16 3 L28 9.5 L16 16 L4 9.5 Z" />
      <path class="face left" d="M4 9.5 L16 16 L16 29 L4 22.5 Z" />
      <path class="face right" d="M28 9.5 L16 16 L16 29 L28 22.5 Z" />
    </g>
  </svg>
</template>

<style scoped>
.scope-mark {
  flex: none;
  display: inline-block;
  vertical-align: middle;
}
.face {
  stroke: rgba(0, 0, 0, 0.35);
}
.cube .top {
  fill: var(--scope-face-top);
}
.cube .left {
  fill: var(--scope-face-left);
}
.cube .right {
  fill: var(--scope-face-right);
}
.prod .top {
  fill: #f6c765;
}
.prod .left {
  fill: var(--scope-prod);
}
.prod .right {
  fill: #a3741f;
}
.off .face {
  fill: none;
  stroke: var(--scope-text-3);
  stroke-width: 1.2;
}
/* The one orchestrated moment: on connection the faces fill in turn. */
.cube .face,
.prod .face {
  animation: face-in 0.35s ease both;
}
.cube .left,
.prod .left {
  animation-delay: 0.12s;
}
.cube .right,
.prod .right {
  animation-delay: 0.24s;
}
@keyframes face-in {
  from {
    fill-opacity: 0;
  }
  to {
    fill-opacity: 1;
  }
}
@media (prefers-reduced-motion: reduce) {
  .face {
    animation: none !important;
  }
}
</style>
