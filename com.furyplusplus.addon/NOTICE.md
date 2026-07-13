# Notices

FuryPlusPlus is an independent project and is not affiliated with, endorsed by, or supported by
VRCFury or its authors.

FuryPlusPlus does **not** include, bundle, or redistribute any part of VRCFury. It patches a
VRCFury installation that is already present in your project, at runtime, in memory only. To
guarantee identical behavior, some of FuryPlusPlus's replacement code paths intentionally mirror
the logic of the VRCFury internals they replace.

## Commercial use of VRCFury

Starting with **VRCFury 1.1351.0** (released 2026-07-07), the VRCFury commercial license prohibits
modifying or patching VRCFury "through any third party, tool, plugin, script, build step, or
service," and prohibits bundling, linking to, or instructing users toward any such tool for use
with a product or service. FuryPlusPlus is exactly such a tool: it patches VRCFury in memory at
runtime.

If you use VRCFury under its **commercial** license — for example, to build or sell avatars for
commercial purposes — then using, bundling, or directing others to FuryPlusPlus alongside VRCFury
1.1351.0 or later would likely violate that license, which terminates automatically on any
violation. Personal, non-commercial use is unaffected: VRCFury's personal license permits
modification.

Confirm which VRCFury version and license govern your use. This notice is informational and is not
legal advice.

VRCFury is (c) Senky — https://vrcfury.com — and is licensed under its own terms:
https://github.com/VRCFury/VRCFury/blob/main/LICENSE.md

## Relationship to QuickFury

FuryPlusPlus is the successor to QuickFury and includes ports of all of its bake-speed patches.
The two packages patch the same VRCFury methods and must never be installed together;
FuryPlusPlus refuses to initialize while QuickFury is present.
