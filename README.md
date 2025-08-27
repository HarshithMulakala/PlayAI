## GameGen: Unity 2D From Prompt

Usage:

```bash
python cli.py new "A forest platformer with enemies and coins" --out GeneratedGame
```

Open `GeneratedGame` in Unity. Run menu: `GameGen > Build From Spec`.

Environment:
- Set `GEMINI_API_KEY` to use Gemini 2.5-Flash; otherwise, local fallbacks are used.

Files:
- `unity_template/` Unity project skeleton with `SpecBootstrapper.cs`.
- `schema/GameSpec2D.schema.json` JSON schema.
- `examples/GameSpec2D.json` Example spec.
- `asset_library/` Place sprites (`.png`) plus `.meta` here; will be copied to `Assets/3P/`.


