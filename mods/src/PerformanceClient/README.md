# TABG Performance Client

Custom-server/offline BepInEx optimization plugin. Do not install it into the Easy Anti-Cheat stock client.

This plugin is incompatible with `TabgInstaller.EnhancedClient`; choose one client rendering/streaming profile rather than loading both.

The default performance profile uses a 1,200 m far plane, 100 m shadows, no AO, volumetric haze, or planar reflections, and native-resolution world and UI rendering. It also provides centralized culling, distance-based remote-player physics LOD, non-allocating projectile/interaction/pickup/camera queries, cached remote rigidbodies, frozen-car early-out, streaming fixes, reduced UI rebuilds, and direct fixed-format network packet parsing.

The Main Menu becomes a black 2D-only scene while its UI remains usable; original 3D previews return in Drip, Store, Battle Pass, and Results. Press F10 for the FPS/frame-time overlay and F8 for the offline Shooting Range.

All settings are generated in `BepInEx/config/tabginstaller.performanceclient.cfg`. Servers using `TabgInstaller.PerformanceServer` with delta snapshots enabled require this client DLL.
