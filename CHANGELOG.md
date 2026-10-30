# Changelog

## 0.1.0 - Initial release

- Loader: hook local prediction, flight-timeout compensation (LoaderRuler-equivalent), crosshair range fix, and aim lead for moving targets.
- Enforcer: dash feedback with stuck fallback, start lead, exit smoothing, ghost dash, and local early end.
- Optional Loader HUD elements and optional gameplay multipliers (vanilla by default).
- Note: with defaults, the LoaderRuler-equivalent deadline roughly doubles the Loader hook's flight time and reach (crosshair ≈ 178 m vs 80 m nominal vanilla) on clients and when hosting/solo; disable `LoaderFlyTimeout` for vanilla reach.
- Client-side only with `NoNeedForSync`; hosts and other players do not need the mod. Hits and damage stay host-authoritative.
