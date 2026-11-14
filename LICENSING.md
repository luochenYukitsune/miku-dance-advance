# Licensing

Copyright (c) 2026 luochen Yukitsune.

MDA's original code and documentation are licensed under the **GNU Affero General Public License, version 3 only** (`AGPL-3.0-only`). The complete license is in [LICENSE](LICENSE). This notice does not grant an "or any later version" option.

Third-party material retains its own copyright notices and license terms. The top-level license does not replace them. In particular:

- `third_party/`: each component's included license and provenance apply. Saba and the retained nanoem component use MIT; Bullet uses zlib; SMAA and FSR1 retain their included permissive notices. Vendor API notices are not a license for proprietary reconstruction algorithms or SDK binaries.
- `compatibility/d3d9-mme-host/upstream/`: the retained MikuDanceStudio source foundation is MIT-licensed. Upstream-derived patches retain the upstream notice. It is a third-party behavioral reconstruction, not original MMD/MMEffect source.
- `native/Mmd.Modern.Bullet.SoftBody/upstream/` and `native/Mmd.Modern.Mikktspace/upstream/`: retain the included Bullet and MikkTSpace notices. Generated altered source copies are identified by the build scripts and their manifests.
- `src/**/LICENSE*`, source headers and inline attribution identify ports or adaptations, including Saba, nanoem, Avalonia, SukiUI and Phosphor. Retain these notices when copying or redistributing those portions. MDA's original additions are covered by the project license subject to the retained third-party terms.
- `src/Mmd.Modern.App/Assets/Fonts/`: the unmodified HarmonyOS Sans fonts use the included **HarmonyOS Sans Fonts License Agreement**, not AGPL, MIT or OFL. They are bundled as part of MDA software, with the full license and provenance. Do not extract and distribute them as a standalone font product.
- Original MMD/MMEffect binaries, original toon textures, user models, motions, projects and effect assets are not granted a license by this repository and are not part of its public source snapshot. Assets supplied by users remain subject to their own authors' terms.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for component versions, source locations, adaptations and further notices. NuGet dependencies are pinned in the project files and lock files; their own package licenses continue to apply.

Optional proprietary SDK/runtime components must be obtained separately under their applicable terms. Publishing source interfaces does not claim that every combination with such components may be redistributed under AGPL. No proprietary SDK binary, FFmpeg executable or original MMD runtime is included in the public source snapshot. A future binary release must satisfy the obligations of the actual components it contains; a copyright notice alone does not substitute for corresponding source where required.
