import os
import json
import shutil
from pathlib import Path
from typing import Dict, List, Optional

import typer
from rich import print
from rich.panel import Panel
from rich.prompt import Prompt

# Load .env file if it exists
try:
    from dotenv import load_dotenv
    load_dotenv()
except ImportError:
    # dotenv not installed, try to load manually
    env_path = Path(".env")
    if env_path.exists():
        with env_path.open("r") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#") and "=" in line:
                    key, value = line.split("=", 1)
                    os.environ[key.strip()] = value.strip()


# --------------------------- Script Templates ------------------------------ #
_PLAYER_CONTROLLER_CS = r"""
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float jumpForce = 7f;
    private Rigidbody2D _rb;
    private BoxCollider2D _collider;
    private bool _isGrounded;

    private void Awake()
    {
        _rb = gameObject.GetComponent<Rigidbody2D>();
        if (_rb == null) _rb = gameObject.AddComponent<Rigidbody2D>();
        _rb.gravityScale = 3f;
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        _collider = gameObject.GetComponent<BoxCollider2D>();
        if (_collider == null) _collider = gameObject.AddComponent<BoxCollider2D>();
    }

    private void Update()
    {
        float x = Input.GetAxisRaw("Horizontal");
        Vector2 v = _rb.velocity;
        v.x = x * moveSpeed;
        _rb.velocity = v;

        if (Input.GetButtonDown("Jump") && _isGrounded)
        {
            _rb.AddForce(Vector2.up * jumpForce, ForceMode2D.Impulse);
        }
    }

    private void OnCollisionEnter2D(Collision2D other)
    {
        foreach (var contact in other.contacts)
        {
            if (Vector2.Dot(contact.normal, Vector2.up) > 0.5f)
            {
                _isGrounded = true;
                break;
            }
        }
    }

    private void OnCollisionExit2D(Collision2D other)
    {
        _isGrounded = false;
    }
}
""";

_ENEMY_AI_CS = r"""
using UnityEngine;

public class EnemyAI : MonoBehaviour
{
    public float patrolAmplitude = 2f;
    public float patrolSpeed = 1f;
    private Vector3 _origin;

    private void Start()
    {
        _origin = transform.position;
        var rb = gameObject.GetComponent<Rigidbody2D>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody2D>();
        rb.gravityScale = 0.5f;
        rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        var col = gameObject.GetComponent<Collider2D>();
        if (col == null) col = gameObject.AddComponent<BoxCollider2D>();
    }

    private void Update()
    {
        float dx = Mathf.Sin(Time.time * patrolSpeed) * patrolAmplitude;
        transform.position = new Vector3(_origin.x + dx, transform.position.y, transform.position.z);
    }
}
""";

_SLIME_AI_CS = r"""
using UnityEngine;

public class SlimeAI : EnemyAI
{
    // Can override behavior in the future
}
""";

_BAT_AI_CS = r"""
using UnityEngine;

public class BatAI : EnemyAI
{
    private void Update()
    {
        // Circle the spawn point
        float radius = 1.5f;
        float speed = 1.2f;
        float x = Mathf.Cos(Time.time * speed) * radius;
        float y = Mathf.Sin(Time.time * speed) * radius;
        transform.localPosition = new Vector3(x, y, 0f);
    }
}
""";

_COLLECTIBLE_CS = r"""
using UnityEngine;

public class Collectible : MonoBehaviour
{
    public int value = 1;

    private void Reset()
    {
        var col = GetComponent<Collider2D>();
        if (col == null) col = gameObject.AddComponent<CircleCollider2D>();
        col.isTrigger = true;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other.gameObject.GetComponent<PlayerController>() != null)
        {
            Destroy(gameObject);
        }
    }
}
""";

_GAME_UI_SCRIPTS_CS = r"""
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GameUIActions
{
    public static void RestartGame()
    {
        var scene = SceneManager.GetActiveScene();
        SceneManager.LoadScene(scene.name);
    }
}
""";


app = typer.Typer(help="GameGen CLI: Generate a Unity 2D project scaffold from a text prompt.")


class GeminiClient:
    """Thin wrapper for Gemini 2.5-Flash calls with graceful local fallbacks."""

    def __init__(self, model_name: str = "gemini-2.5-flash") -> None:
        self.model_name = model_name
        self.api_key = os.environ.get("GEMINI_API_KEY")
        self._client = None

        if self.api_key:
            print(f"[green]Gemini API key found: {self.api_key[:8]}...[/green]")
            try:
                # Prefer new client name if available; otherwise fall back to google-generativeai
                # These imports are optional and only used if installed.
                import google.generativeai as genai  # type: ignore

                genai.configure(api_key=self.api_key)
                self._client = genai.GenerativeModel(self.model_name)
                print(f"[green]Gemini client initialized with model: {self.model_name}[/green]")
            except Exception as exc:  # pragma: no cover - optional dependency
                print(f"[yellow]Gemini SDK not available or failed to init ({exc}). Using local fallback.[/yellow]")
                self._client = None
        else:
            print("[yellow]No GEMINI_API_KEY found in environment. Using local fallback.[/yellow]")

    # ------------------------------ Public API ------------------------------ #
    def generate_spec(self, user_prompt: str) -> Dict:
        """Return a GameSpec2D JSON document from user's text prompt."""
        if self._client is None:
            return self._fallback_spec(user_prompt)


        try:
            # Use structured output to ensure valid JSON
            full_prompt = f"""Create a Unity 2D game specification based on this request: {user_prompt}

The specification should include:
- A game title
- At least one scene with a name, background image, and tilemap
- A player character with sprite, controller script, and spawn coordinates
- Some enemies with sprites, controller scripts, and spawn coordinates  
- Some collectibles with sprites and spawn coordinates
- UI elements like labels and buttons

Make sure all arrays have at least one item and coordinates are arrays of 2 numbers."""
            
            # Define the response schema for structured output (simplified for google-generativeai)
            response_schema = {
                "type": "object",
                "properties": {
                    "game": {
                        "type": "object",
                        "properties": {
                            "title": {"type": "string"}
                        }
                    },
                    "scenes": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "name": {"type": "string"},
                                "background": {"type": "string"},
                                "tilemap": {"type": "string"},
                                "player": {
                                    "type": "object",
                                    "properties": {
                                        "sprite": {"type": "string"},
                                        "controller": {"type": "string"},
                                        "spawn": {
                                            "type": "array",
                                            "items": {"type": "number"}
                                        }
                                    }
                                },
                                "enemies": {
                                    "type": "array",
                                    "items": {
                                        "type": "object",
                                        "properties": {
                                            "sprite": {"type": "string"},
                                            "controller": {"type": "string"},
                                            "spawn": {
                                                "type": "array",
                                                "items": {"type": "number"}
                                            }
                                        }
                                    }
                                },
                                "collectibles": {
                                    "type": "array",
                                    "items": {
                                        "type": "object",
                                        "properties": {
                                            "sprite": {"type": "string"},
                                            "spawn": {
                                                "type": "array",
                                                "items": {"type": "number"}
                                            }
                                        }
                                    }
                                },
                                "ui": {
                                    "type": "array",
                                    "items": {
                                        "type": "object",
                                        "properties": {
                                            "type": {"type": "string"},
                                            "text": {"type": "string"},
                                            "anchor": {"type": "string"},
                                            "onClick": {"type": "string"}
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            
            rsp = self._client.generate_content(
                full_prompt,
                generation_config={
                    "response_mime_type": "application/json",
                    "response_schema": response_schema
                }
            )
            
            # With structured output, we can use the parsed response directly
            if hasattr(rsp, 'parsed') and rsp.parsed:
                print(f"[green]Got parsed spec from Gemini[/green]")
                return rsp.parsed
            else:
                # Fallback to text parsing if parsed is not available
                text = rsp.text.strip()
                print(f"[blue]Gemini response length: {len(text)}[/blue]")
                if len(text) < 100:
                    print(f"[blue]Gemini response preview: {repr(text)}[/blue]")
                
                if not text:
                    print("[yellow]Gemini returned empty response. Using fallback.[/yellow]")
                    return self._fallback_spec(user_prompt)
                
                try:
                    data = json.loads(text)
                    print(f"[green]Parsed JSON spec from Gemini[/green]")
                    return data
                except json.JSONDecodeError as exc:
                    print(f"[yellow]Failed to parse JSON from Gemini: {exc}[/yellow]")
                    return self._fallback_spec(user_prompt)
                
        except Exception as exc:  # pragma: no cover
            print(f"[yellow]Gemini spec generation failed: {exc}. Using fallback.[/yellow]")
            return self._fallback_spec(user_prompt)

    def generate_scripts(self, spec: Dict) -> Dict[str, str]:
        """Return a dict of {relative_path: file_content} for Unity C# scripts.
        Dynamically determines which scripts are required from the spec and generates each file individually.
        """
        required_paths, onclick_names = self._determine_required_scripts(spec)
        results: Dict[str, str] = {}

        # If no LLM, defer to dynamic fallback
        if self._client is None:
            print("[yellow]Using dynamic fallback script generation (no Gemini client)[/yellow]")
            return self._fallback_scripts(spec)

        # Generate each required script one-by-one via Gemini
        for rel_path in sorted(required_paths):
            file_name = Path(rel_path).name
            try:
                # UI actions file is generated locally (strongly-typed to spec)
                if file_name == "GameUIScripts.cs":
                    results[rel_path] = self._build_ui_actions_script(sorted(onclick_names))
                    continue

                prompt = (
                    "You are a Unity C# generator. Create a complete, compilable C# script suitable for Unity 2D. "
                    f"The file name is '{file_name}'. Consider this game spec JSON to tailor behavior: \n\n"
                    f"{json.dumps(spec, indent=2)}\n\n"
                    "Rules:\n"
                    "- Return ONLY raw C# code (no markdown fences or commentary).\n"
                    "- Use only Unity built-in APIs.\n"
                    "- For controllers, include sensible defaults (Rigidbody2D, Collider2D if needed).\n"
                )
                rsp = self._client.generate_content(prompt)
                code = (rsp.text or "").strip()
                if not code:
                    raise ValueError("Empty response from Gemini")
                results[rel_path] = code
            except Exception as exc:
                print(f"[yellow]Gemini failed to generate {file_name}: {exc}. Using template/stub.[/yellow]")
                tmpl = self._template_for_known(file_name)
                if tmpl is None:
                    results[rel_path] = self._default_stub_for(file_name)
                else:
                    results[rel_path] = tmpl

        # If any specialized enemy that extends EnemyAI template was used, ensure EnemyAI base exists in results
        if any(Path(p).name in ("SlimeAI.cs", "BatAI.cs") for p in results.keys()) and \
           "Assets/Scripts/EnemyAI.cs" not in results:
            results["Assets/Scripts/EnemyAI.cs"] = _ENEMY_AI_CS

        return results

    def _determine_required_scripts(self, spec: Dict) -> tuple[set, set]:
        required_paths: set = set()
        onclick_names: set = set()

        for scene in spec.get("scenes", []) or []:
            player = scene.get("player") or {}
            controller = player.get("controller")
            if isinstance(controller, str) and controller.endswith(".cs"):
                required_paths.add(f"Assets/Scripts/{controller}")

            for enemy in scene.get("enemies", []) or []:
                e_ctrl = enemy.get("controller")
                if isinstance(e_ctrl, str) and e_ctrl.endswith(".cs"):
                    required_paths.add(f"Assets/Scripts/{e_ctrl}")

            # Collectible behavior helper if any collectibles are present
            if scene.get("collectibles"):
                required_paths.add("Assets/Scripts/Collectible.cs")

            # UI actions: gather any onClick names
            for ui in scene.get("ui", []) or []:
                name = ui.get("onClick")
                if isinstance(name, str) and name.strip():
                    onclick_names.add(name.strip())

        # Only include GameUIScripts.cs if we actually have any actions
        if onclick_names:
            required_paths.add("Assets/Scripts/GameUIScripts.cs")

        return required_paths, onclick_names

    def _build_ui_actions_script(self, action_names: List[str]) -> str:
        # Always include RestartGame if requested; otherwise generate stubs
        methods: List[str] = []
        for name in action_names:
            if name == "RestartGame":
                methods.append(
                    """
    public static void RestartGame()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        UnityEngine.SceneManagement.SceneManager.LoadScene(scene.name);
    }
                    """.strip()
                )
            else:
                methods.append(
                    f"""
    public static void {name}()
    {{
        UnityEngine.Debug.Log("UI Action invoked: {name}");
    }}
                    """.strip()
                )

        body = "\n\n".join(methods)
        return (
            "using UnityEngine;\n"
            "using UnityEngine.SceneManagement;\n\n"
            "public static class GameUIActions\n{\n"
            f"{body}\n"
            "}\n"
        )

    def _default_stub_for(self, file_name: str) -> str:
        class_name = file_name.replace(".cs", "").strip()
        return (
            "using UnityEngine;\n\n"
            f"public class {class_name} : MonoBehaviour\n"
            "{\n"
            "    private void Awake() { }\n"
            "    private void Update() { }\n"
            "}\n"
        )

    def _template_for_known(self, file_name: str) -> Optional[str]:
        if file_name == "PlayerController.cs":
            return _PLAYER_CONTROLLER_CS
        if file_name == "EnemyAI.cs":
            return _ENEMY_AI_CS
        if file_name == "SlimeAI.cs":
            return _SLIME_AI_CS
        if file_name == "BatAI.cs":
            return _BAT_AI_CS
        if file_name == "Collectible.cs":
            return _COLLECTIBLE_CS
        if file_name == "GameUIScripts.cs":
            # Use dynamic build instead; kept here for safety
            return _GAME_UI_SCRIPTS_CS
        return None

    # ------------------------------ Fallbacks ------------------------------- #
    def _fallback_spec(self, user_prompt: str) -> Dict:
        # Minimal example spec resembling the provided example
        return {
            "game": {"title": "Forest Platformer"},
            "scenes": [
                {
                    "name": "Level1",
                    "background": "forest_bg.png",
                    "tilemap": "grass_tileset.png",
                    "player": {
                        "sprite": "hero_idle.png",
                        "controller": "PlayerController.cs",
                        "spawn": [0, 0],
                    },
                    "enemies": [
                        {"sprite": "slime.png", "controller": "SlimeAI.cs", "spawn": [5, 0]},
                        {"sprite": "bat.png", "controller": "BatAI.cs", "spawn": [8, 3]},
                    ],
                    "collectibles": [
                        {"sprite": "coin.png", "spawn": [2, 1]},
                        {"sprite": "coin.png", "spawn": [7, 2]},
                        {"sprite": "heart.png", "spawn": [10, 4]},
                    ],
                    "ui": [
                        {"type": "label", "text": "Score: 0", "anchor": "top_left"},
                        {"type": "button", "text": "Restart", "anchor": "bottom_right", "onClick": "RestartGame"},
                    ],
                }
            ],
        }

    def _fallback_scripts(self, spec: Dict) -> Dict[str, str]:
        scripts: Dict[str, str] = {}
        required_paths, onclick_names = self._determine_required_scripts(spec)

        for rel_path in sorted(required_paths):
            file_name = Path(rel_path).name
            if file_name == "GameUIScripts.cs":
                scripts[rel_path] = self._build_ui_actions_script(sorted(onclick_names))
                continue

            tmpl = self._template_for_known(file_name)
            if tmpl is None:
                scripts[rel_path] = self._default_stub_for(file_name)
            else:
                scripts[rel_path] = tmpl

        # Ensure base EnemyAI if we provided Slime/Bat templates
        if any(Path(p).name in ("SlimeAI.cs", "BatAI.cs") for p in scripts.keys()) and \
           "Assets/Scripts/EnemyAI.cs" not in scripts:
            scripts["Assets/Scripts/EnemyAI.cs"] = _ENEMY_AI_CS

        print("[yellow]Using fallback scripts (generated per spec) since Gemini generation failed[/yellow]")
        return scripts


def copy_unity_template(template_dir: Path, out_dir: Path) -> None:
    if out_dir.exists():
        print(f"[yellow]Output directory exists: {out_dir}. Files may be overwritten.[/yellow]")

    shutil.copytree(template_dir, out_dir, dirs_exist_ok=True)


def purge_template_scripts(out_dir: Path) -> None:
    """Remove default template scripts so only spec-required scripts remain.
    Keeps helper scripts like UIInvokeStatic.cs.
    """
    scripts_dir = out_dir / "Assets" / "Scripts"
    if not scripts_dir.exists():
        return
    for path in scripts_dir.glob("*.cs"):
        if path.name == "UIInvokeStatic.cs":
            continue
        try:
            path.unlink()
        except Exception:
            pass


def write_spec(out_dir: Path, spec: Dict) -> Path:
    dst = out_dir / "Assets" / "Resources" / "Bootstrap"
    dst.mkdir(parents=True, exist_ok=True)
    spec_path = dst / "GameSpec2D.json"
    with spec_path.open("w", encoding="utf-8") as f:
        json.dump(spec, f, ensure_ascii=False, indent=2)
    return spec_path


def write_scripts(out_dir: Path, scripts: Dict[str, str]) -> List[Path]:
    written: List[Path] = []
    for rel_path, content in scripts.items():
        dst = out_dir / rel_path
        dst.parent.mkdir(parents=True, exist_ok=True)
        with dst.open("w", encoding="utf-8", newline="\n") as f:
            f.write(content)
        written.append(dst)
    return written


def copy_assets(library_dir: Path, out_dir: Path) -> List[Path]:
    dst_root = out_dir / "Assets" / "3P"
    dst_root.mkdir(parents=True, exist_ok=True)
    written: List[Path] = []
    if not library_dir.exists():
        print(f"[yellow]Asset library not found: {library_dir}. Skipping copy.[/yellow]")
        return written

    for item in library_dir.iterdir():
        if item.is_file() and (item.suffix.lower() == ".png" or item.suffix.lower() == ".meta"):
            dst = dst_root / item.name
            shutil.copy2(item, dst)
            written.append(dst)
    return written


@app.command("new")
def cmd_new(
    prompt: str = typer.Option(..., "--prompt", "-p", help="Text prompt to describe the game"),
    out: Path = typer.Option(Path("GeneratedGame"), "--out", help="Output directory for the Unity project"),
    yes: bool = typer.Option(False, "--yes", "-y", help="Skip prompt for missing assets and proceed"),
):
    """Generate a new Unity 2D project from a prompt using Gemini 2.5-Flash."""

    # Resolve repo paths
    root = Path(__file__).parent
    template_dir = root / "unity_template"
    library_dir = root / "asset_library"



    print(Panel.fit(f"[bold]GameGen[/bold] will create a Unity project at [cyan]{out}[/cyan]"))

    # 1) Copy Unity template
    copy_unity_template(template_dir, out)
    print("[green]Template copied.[/green]")
    purge_template_scripts(out)
    print("[green]Cleaned default template scripts.[/green]")

    # 2) Call Gemini to turn prompt into spec
    gem = GeminiClient()
    spec = gem.generate_spec(prompt)

    # Optional: validate against schema if available
    schema_path = root / "schema" / "GameSpec2D.schema.json"
    if schema_path.exists():
        try:
            import jsonschema  # type: ignore

            with schema_path.open("r", encoding="utf-8") as f:
                schema = json.load(f)
            jsonschema.validate(instance=spec, schema=schema)
        except Exception as exc:  # pragma: no cover - validation is best-effort
            print(f"[yellow]Spec validation warning: {exc}[/yellow]")

    # 3) Write spec into project
    spec_path = write_spec(out, spec)
    print(f"[green]Wrote spec:[/green] {spec_path}")

    # 4) Call Gemini with spec to produce scripts
    scripts = gem.generate_scripts(spec)
    script_paths = write_scripts(out, scripts)
    print(f"[green]Wrote {len(script_paths)} scripts under Assets/Scripts/[/green]")

    # 5) Copy .png assets (+ .meta) from asset_library → Assets/3P/
    if not library_dir.exists() and not yes:
        proceed = Prompt.ask(
            "Asset library is missing. Proceed anyway? (sprites will be missing)", choices=["y", "n"], default="y"
        )
        if proceed.lower() != "y":
            raise typer.Abort()

    copied = copy_assets(library_dir, out)
    print(f"[green]Copied {len(copied)} asset files to Assets/3P/[/green]")

    print(Panel.fit("Project ready. Open it in Unity, then run: Menu → GameGen → Build From Spec"))


if __name__ == "__main__":
    app()
