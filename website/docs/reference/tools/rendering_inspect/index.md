---
title: "rendering_inspect tools"
sidebar_label: "rendering_inspect"
description: "MCP for Unity tools in the rendering_inspect group."
---

# `rendering_inspect` tools

Read-only renderer, material, texture, Shader Graph, render-contract & probe inspection

- **[`inspect_material`](./inspect_material.md)** — Inspect one material by exact asset path, including path/GUID, shader identity and kind, typed current/default values, texture path/GUID and tiling, keywords, passes, queue/surface state, GI/instancing/SRP Batcher evidence, and paged liv…
- **[`inspect_render_target`](./inspect_render_target.md)** — Inspect the actual render-owner closure for a scene object: renderers, submeshes, material slots, material property blocks, LOD membership, lightmap state, and package/asset ownership.
- **[`inspect_shader_graph`](./inspect_shader_graph.md)** — Inspect a ShaderLab, Shader Graph, or Sub Graph asset by exact path.
- **[`inspect_texture`](./inspect_texture.md)** — Inspect one texture by exact asset path.
- **[`profile_render_target`](./profile_render_target.md)** — Profile one scene render target with static renderer/material/pass/mesh evidence and a paged Frame Debugger snapshot filtered to its renderer instance IDs when Frame Debugger data is available.
- **[`render_probe`](./render_probe.md)** — Capture a deterministic color or wireframe render probe from an existing camera.
- **[`sample_material`](./sample_material.md)** — Render one exact material in an isolated, deterministic Editor preview.
- **[`validate_render_contract`](./validate_render_contract.md)** — Validate the renderer-material-shader-texture closure for an exact material or scene target.
