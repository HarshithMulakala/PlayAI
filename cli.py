import json
import os
import re
import sys
from datetime import datetime
from pathlib import Path
from typing import Optional
import shutil

import google.generativeai as genai
import typer
from jsonschema import Draft202012Validator, validate
from rich.console import Console
from rich.panel import Panel
from PIL import Image

from dotenv import load_dotenv

load_dotenv()


app = typer.Typer(help="Generate a Unity 2D MVP game spec JSON using Gemini and save to output directory.")
console = Console()


DEFAULT_MODEL_NAME = os.getenv("GEMINI_MODEL", "gemini-2.5-flash")
DEFAULT_SCHEMA_PATH = Path("schema/GameSpec2D.schema.json")
DEFAULT_ASSET_DIR = Path("asset_library")
DEFAULT_UNITY_TEMPLATE_DIR = Path("unity_template")


def load_schema(schema_path: Path) -> dict:
    """Load and return the JSON schema as a Python dict."""
    try:
        with schema_path.open("r", encoding="utf-8") as f:
            schema = json.load(f)
    except FileNotFoundError:
        raise typer.Exit(code=2)

    # Validate the schema itself
    Draft202012Validator.check_schema(schema)
    return schema


def ensure_api_key() -> str:
    """Read GEMINI_API_KEY from environment or exit with error."""
    api_key = os.getenv("GEMINI_API_KEY")
    if not api_key:
        console.print(
            Panel.fit(
                "Environment variable GEMINI_API_KEY is not set.",
                title="Missing API key",
                subtitle="Set GEMINI_API_KEY and try again",
                style="red",
            )
        )
        raise typer.Exit(code=2)
    return api_key


def sanitize_filename(name: str) -> str:
    """Create a filesystem-safe filename segment from arbitrary text."""
    name = name.strip().lower()
    name = re.sub(r"[^a-z0-9\-_]+", "-", name)
    name = re.sub(r"-+", "-", name).strip("-")
    return name or "gamespec"


def extract_json(text: str) -> str:
    """Robustly extract a JSON object or array string from model output."""
    stripped = text.strip()

    # Fast paths
    if (stripped.startswith("{") and stripped.endswith("}")) or (
        stripped.startswith("[") and stripped.endswith("]")
    ):
        return stripped

    # Strip fenced code blocks (support both object and array)
    fenced_match = re.search(r"```(?:json)?\s*([\s\S]*?)\s*```", text, flags=re.IGNORECASE)
    if fenced_match:
        candidate = fenced_match.group(1).strip()
        if (candidate.startswith("{") and candidate.endswith("}")) or (
            candidate.startswith("[") and candidate.endswith("]")
        ):
            return candidate

    # Try to find outermost JSON array
    first_b = stripped.find("[")
    last_b = stripped.rfind("]")
    if first_b != -1 and last_b != -1 and last_b > first_b:
        return stripped[first_b : last_b + 1]

    # Fallback: best-effort to find outermost object
    first = stripped.find("{")
    last = stripped.rfind("}")
    if first != -1 and last != -1 and last > first:
        return stripped[first : last + 1]

    return stripped


def call_gemini(model_name: str, user_prompt: str, schema: dict, assets_catalog: list[dict]) -> dict:
    """Call Gemini and return the parsed JSON object."""
    api_key = ensure_api_key()
    genai.configure(api_key=api_key)

    system_instruction = (
        "You are an expert Unity 2D technical game designer. "
        "Generate a single JSON object that fully specifies a complete, minimal, shippable Unity 2D MVP game. "
        "The JSON MUST strictly conform to the provided JSON Schema. "
        "Do not include any explanations, comments, or additional text—only output the JSON object."
    )

    model = genai.GenerativeModel(
        model_name=model_name,
        system_instruction=system_instruction,
        generation_config={
            # Encourage raw JSON output
            "response_mime_type": "application/json",
        },
    )

    prompt = (
        "Using the following JSON Schema, generate a valid game specification object that fully describes a Unity 2D game MVP matching the user's idea.\n\n"
        f"User idea:\n{user_prompt}\n\n"
        "You MUST ONLY reference sprite assets that exist in the provided asset library.\n"
        "When setting any sprite path (including UI sprites), the value MUST be exactly one of the 'path' fields from the asset library list below.\n"
        "Use the width/height metadata (in pixels) to choose appropriate transform scale and placement.\n"
        "For physics.colliders, include 'autoSize' (default true), and when needed 'size'/'radius' and 'offset' to fine-tune alignment.\n\n"
        "For every script entry in 'scripts', ADD a 'description' field with a highly specific explanation of what the script should accomplish, including any physics, input, collision, and interactions.\n\n"
        "Also populate project-level engine settings (tags, layers, sortingLayers, defaultPixelsPerUnit) under game.settings.\n"
        "Populate per-object 'tag' and 'layer' where relevant (Player, Enemy, Coin, Spike, Ground).\n"
        "For circle colliders, include 'radius'.\n"
        "For scenes, include 'camera' (orthographic, size, position) and 'uiSettings' (Canvas render mode and referenceResolution).\n"
        "For UI rectTransform, include 'sizeDelta' so UI is visible and properly sized.\n"
        "Place dynamic characters and items at plausible positions above 'Ground' objects/layers so they rest on top without overlap.\n"
        "For scripts, use 'parameters' to expose public fields (movement speeds, jumpForce, damageAmount, scoreValue, patrol distances, layer masks).\n\n"
        "Asset library (allowed assets):\n"
        f"{json.dumps(assets_catalog, indent=2)}\n\n"
        "JSON Schema (draft 2020-12):\n"
        f"{json.dumps(schema, indent=2)}\n"
        "Return only the JSON object."
    )

    response = model.generate_content(prompt)
    text = getattr(response, "text", None) or ""
    json_text = extract_json(text)

    try:
        data = json.loads(json_text)
    except json.JSONDecodeError as exc:
        console.print(Panel.fit(str(text)[:2000], title="Model output (truncated)", style="yellow"))
        console.print(f"Failed to parse JSON from model output: {exc}", style="red")
        raise typer.Exit(code=1)

    return data


def validate_against_schema(instance: dict, schema: dict) -> None:
    """Validate instance against schema or exit with error details."""
    try:
        validate(instance=instance, schema=schema)
    except Exception as exc:
        console.print("Schema validation failed:", style="red")
        console.print(str(exc), style="red")
        raise typer.Exit(code=1)


def scan_asset_library(asset_dir: Path) -> list[dict]:
    """Scan the asset library for images and return metadata used for prompting.

    Each item contains: { path, filename, width, height, ext }
    """
    if not asset_dir.exists():
        console.print(f"Asset library directory not found: {asset_dir}", style="red")
        raise typer.Exit(code=2)

    exts = {".png", ".jpg", ".jpeg", ".gif"}
    items: list[dict] = []
    for p in sorted(asset_dir.iterdir()):
        if p.is_file() and p.suffix.lower() in exts:
            try:
                with Image.open(p) as img:
                    width, height = img.size
            except Exception:
                continue
            rel_path = Path(asset_dir.name) / p.name
            items.append(
                {
                    "path": rel_path.as_posix(),
                    "filename": p.name,
                    "width": width,
                    "height": height,
                    "ext": p.suffix.lower(),
                }
            )
    if not items:
        console.print("No image assets found in asset_library. The model will have no sprites to reference.", style="yellow")
    return items


def collect_referenced_asset_paths(spec: dict) -> set[str]:
    """Collect all asset paths referenced in the spec for sprites and UI images."""
    paths: set[str] = set()
    game = spec.get("game", {})
    for scene in game.get("scenes", []) or []:
        # gameObjects sprites
        for go in scene.get("gameObjects", []) or []:
            sprite = go.get("sprite") or {}
            path = sprite.get("path")
            if isinstance(path, str):
                paths.add(path)
        # UI sprites
        for ui in scene.get("ui", []) or []:
            path = ui.get("sprite")
            if isinstance(path, str):
                paths.add(path)
    return paths


def validate_asset_usage(spec: dict, allowed_paths: set[str]) -> None:
    """Ensure all referenced asset paths are from the allowed list."""
    referenced = collect_referenced_asset_paths(spec)
    invalid = sorted(p for p in referenced if p not in allowed_paths)
    if invalid:
        console.print("Invalid asset paths detected in generated spec:", style="red")
        for p in invalid:
            console.print(f" - {p}", style="red")
        console.print("Allowed paths are:", style="yellow")
        for p in sorted(allowed_paths):
            console.print(f" - {p}", style="yellow")
        raise typer.Exit(code=1)


def collect_script_specs(spec: dict) -> list[dict]:
    """Collect unique script definitions with usage context from spec."""
    scripts: dict[str, dict] = {}
    game = spec.get("game", {})
    for scene in game.get("scenes", []) or []:
        scene_name = scene.get("name") or "Scene"
        # GameObject scripts
        for go in scene.get("gameObjects", []) or []:
            owner = {
                "ownerType": "gameObject",
                "ownerName": go.get("name") or go.get("id") or "GameObject",
                "sceneName": scene_name,
            }
            for sc in (go.get("scripts") or []):
                if not isinstance(sc, dict):
                    continue
                name = str(sc.get("name") or "Script").strip()
                key = name
                if key not in scripts:
                    scripts[key] = {
                        "name": name,
                        "parametersExamples": [],
                        "usedBy": [],
                        "descriptions": [],
                    }
                scripts[key]["usedBy"].append(owner)
                if isinstance(sc.get("parameters"), dict) and sc["parameters"]:
                    scripts[key]["parametersExamples"].append(sc["parameters"])
                if isinstance(sc.get("description"), str) and sc["description"].strip():
                    scripts[key]["descriptions"].append(sc["description"].strip())
        # UI scripts
        for ui in scene.get("ui", []) or []:
            owner = {
                "ownerType": "uiElement",
                "ownerName": ui.get("name") or ui.get("id") or "UIElement",
                "sceneName": scene_name,
            }
            for sc in (ui.get("scripts") or []):
                if not isinstance(sc, dict):
                    continue
                name = str(sc.get("name") or "Script").strip()
                key = name
                if key not in scripts:
                    scripts[key] = {
                        "name": name,
                        "parametersExamples": [],
                        "usedBy": [],
                        "descriptions": [],
                    }
                scripts[key]["usedBy"].append(owner)
                if isinstance(sc.get("parameters"), dict) and sc["parameters"]:
                    scripts[key]["parametersExamples"].append(sc["parameters"])
                if isinstance(sc.get("description"), str) and sc["description"].strip():
                    scripts[key]["descriptions"].append(sc["description"].strip())

    return list(scripts.values())


def sanitize_csharp_filename(script_name: str) -> str:
    """Make a safe .cs filename, preserving provided names when possible."""
    # Keep base name only
    base = Path(script_name).name if script_name else ""
    base = base or "GeneratedScript.cs"
    if not base.lower().endswith(".cs"):
        base = f"{base}.cs"
    # Replace disallowed characters but preserve original casing
    safe = re.sub(r"[^A-Za-z0-9_.-]", "_", base)
    # Avoid leading dots or dashes
    safe = safe.lstrip(".-") or "GeneratedScript.cs"
    return safe


def prepare_unity_project(
    unity_template_dir: Path,
    unity_out_dir: Path,
    assets_catalog: list[dict],
    asset_source_dir: Path,
    spec_file: Path,
) -> None:
    """Copy Unity template and required files into a new project folder.

    - Copies the entire unity_template to unity_out_dir
    - Copies all referenced assets from asset_source_dir into Assets/asset_library
    - Copies the spec JSON to Assets/Specs/
    """
    if not unity_template_dir.exists():
        raise FileNotFoundError(f"Unity template not found: {unity_template_dir}")

    # Copy template
    if unity_out_dir.exists():
        # Do not nuke existing; allow re-use. Copy missing/overwrite files.
        pass
    else:
        shutil.copytree(unity_template_dir, unity_out_dir)

    # Ensure asset and spec dirs
    assets_dst_dir = unity_out_dir / "Assets" / asset_source_dir.name
    specs_dst_dir = unity_out_dir / "Assets" / "Specs"
    assets_dst_dir.mkdir(parents=True, exist_ok=True)
    specs_dst_dir.mkdir(parents=True, exist_ok=True)

    # Copy assets that are referenced
    referenced = {a["filename"] for a in assets_catalog}
    for item in assets_catalog:
        src = asset_source_dir / item["filename"]
        dst = assets_dst_dir / src.name
        if src.exists():
            shutil.copy2(src, dst)

    # Copy spec
    shutil.copy2(spec_file, specs_dst_dir / spec_file.name)


def call_gemini_generate_scripts(model_name: str, spec: dict, script_specs: list[dict]) -> list[dict]:
    """Ask Gemini to generate C# scripts. Returns list of {filename, content}."""
    api_key = ensure_api_key()
    genai.configure(api_key=api_key)

    system_instruction = (
        "You are a senior Unity C# gameplay programmer. "
        "Generate C# MonoBehaviour scripts for the provided script specifications. "
        "Follow Unity best practices, avoid external packages, and only use standard Unity APIs. "
        "Return ONLY a JSON array of objects with fields 'filename' and 'content'. "
        "Each file should be a complete compilable .cs file. "
        "Write defensive code: if serialized references are not assigned, gracefully auto-resolve them at runtime (e.g., find children by name, find components on the same GameObject, or create helper child objects). "
        "Do not hard-crash on missing references; prefer early null checks and safe fallbacks."
    )

    model = genai.GenerativeModel(
        model_name=model_name,
        system_instruction=system_instruction,
        generation_config={
            "response_mime_type": "application/json",
        },
    )

    prompt = (
        "Create one reusable script per unique script name in the list below. "
        "Use the script 'name' as the C# class name (converted to a valid identifier). "
        "If parametersExamples are provided, expose them as [SerializeField] fields with reasonable defaults, and match names where sensible. "
        "Use the provided descriptions (if any) as the authoritative behavioral spec for the script. "
        "Assume standard Unity 2D physics where appropriate. "
        "Implement input using the legacy Input Manager APIs (UnityEngine.Input). Do NOT use UnityEngine.InputSystem. "
        "If a LayerMask is needed (e.g., ground), define a [SerializeField] private LayerMask named 'groundMask' when possible (importer auto-wires by name). "
        "If a Transform is needed for ground probing, define a [SerializeField] private Transform named 'groundCheck'; if null at runtime, attempt to find a child named 'GroundCheck' or create one slightly below the sprite. "
        "When referencing components (Rigidbody2D/Animator/SpriteRenderer/Collider2D), get them lazily via GetComponent if fields are null. "
        "Never assume references are pre-assigned. "
        "Only output the JSON array, nothing else.\n\n"
        "Script specifications (unique names with usage contexts):\n"
        f"{json.dumps(script_specs, indent=2)}\n\n"
        "For context, here is the full game spec (use it to tailor required fields and logic to the actual scene objects, sizes, and tags/layers):\n"
        f"{json.dumps(spec, indent=2)}\n"
    )

    response = model.generate_content(prompt)
    text = getattr(response, "text", None) or ""
    json_text = extract_json(text)
    try:
        files = json.loads(json_text)
    except json.JSONDecodeError as exc:
        console.print(Panel.fit(str(text)[:2000], title="Scripts output (truncated)", style="yellow"))
        console.print(f"Failed to parse JSON for scripts: {exc}", style="red")
        raise typer.Exit(code=1)

    if not isinstance(files, list):
        console.print("Expected a JSON array of files for scripts.", style="red")
        raise typer.Exit(code=1)
    return files


@app.command()
def generate(
    prompt: Optional[str] = typer.Option(None, "--prompt", "-p", help="Natural language description of the game to generate."),
    prompt_file: Optional[Path] = typer.Option(None, "--prompt-file", "-f", help="Path to a file containing the prompt."),
    outdir: Path = typer.Option(Path("output"), "--outdir", "-o", help="Directory where the JSON spec will be written."),
    schema_path: Path = typer.Option(DEFAULT_SCHEMA_PATH, "--schema", help="Path to the JSON Schema file."),
    model: str = typer.Option(DEFAULT_MODEL_NAME, "--model", help="Gemini model name. Defaults to env GEMINI_MODEL or gemini-2.5-flash."),
    asset_dir: Path = typer.Option(DEFAULT_ASSET_DIR, "--assets", "-a", help="Directory containing the allowed sprite assets."),
    scripts: bool = typer.Option(True, "--scripts/--no-scripts", help="Also generate C# scripts specified by the spec."),
    prepare_unity: bool = typer.Option(True, "--prepare-unity/--no-prepare-unity", help="Copy Unity template and assets to a ready-to-open Unity project."),
    unity_template: Path = typer.Option(DEFAULT_UNITY_TEMPLATE_DIR, "--unity-template", help="Path to the Unity template directory."),
    unity_out: Path = typer.Option(Path("output/UnityProject"), "--unity-out", help="Destination directory for the prepared Unity project."),
):
    """Generate a game spec JSON via Gemini and write it to the output directory."""
    if not prompt and not prompt_file:
        console.print("Provide --prompt or --prompt-file, or pipe text via STDIN.", style="yellow")
        if not sys.stdin.isatty():
            prompt = sys.stdin.read().strip()
        else:
            raise typer.Exit(code=2)

    if prompt_file and not prompt:
        try:
            prompt = prompt_file.read_text(encoding="utf-8")
        except FileNotFoundError:
            console.print(f"Prompt file not found: {prompt_file}", style="red")
            raise typer.Exit(code=2)

    assert prompt is not None

    schema = load_schema(schema_path)
    assets_catalog = scan_asset_library(asset_dir)
    allowed_paths = {a["path"] for a in assets_catalog}

    console.print(Panel.fit("Contacting Gemini to generate game spec...", title="Generating", style="cyan"))
    data = call_gemini(model, prompt, schema, assets_catalog)

    console.print("Validating against schema...", style="cyan")
    validate_against_schema(data, schema)

    console.print("Validating asset usage...", style="cyan")
    validate_asset_usage(data, allowed_paths)

    # Optional script generation
    if scripts:
        console.print("Collecting script specifications...", style="cyan")
        script_specs = collect_script_specs(data)
        if script_specs:
            console.print(Panel.fit("Generating C# scripts...", title="Scripts", style="cyan"))
            files = call_gemini_generate_scripts(model, data, script_specs)
            scripts_dir = outdir / "scripts"
            scripts_dir.mkdir(parents=True, exist_ok=True)
            written = 0
            for file in files:
                if not isinstance(file, dict):
                    continue
                raw_name = str(file.get("filename") or "")
                content = file.get("content")
                if not content:
                    continue
                safe_name = sanitize_csharp_filename(raw_name or (file.get("name") or "Script"))
                target = scripts_dir / safe_name
                with target.open("w", encoding="utf-8") as f:
                    f.write(content)
                written += 1
            console.print(Panel.fit(f"Wrote {written} script file(s) to: {scripts_dir}", title="Scripts", style="green"))
        else:
            console.print("No script specifications found in the game spec.", style="yellow")

    # Determine filename
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    title = (
        data.get("game", {})
        .get("metadata", {})
        .get("title", "Unity2DGame")
    )
    safe_title = sanitize_filename(str(title))
    filename = f"{safe_title}-{timestamp}.json"

    outdir.mkdir(parents=True, exist_ok=True)
    output_path = outdir / filename

    with output_path.open("w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    console.print(Panel.fit(f"Wrote game spec to: {output_path}", title="Success", style="green"))

    # Prepare Unity project directory with template, assets, and spec
    if prepare_unity:
        console.print(Panel.fit("Preparing Unity project...", title="Unity", style="cyan"))
        try:
            prepare_unity_project(
                unity_template_dir=unity_template,
                unity_out_dir=unity_out,
                assets_catalog=assets_catalog,
                asset_source_dir=asset_dir,
                spec_file=output_path,
            )
            console.print(Panel.fit(f"Unity project ready at: {unity_out}", title="Unity", style="green"))
            console.print(
                "Open this folder in Unity, then run: Tools → AI → Import From Spec... (pick the copied spec and your output/scripts)",
                style="cyan",
            )
        except Exception as exc:
            console.print(f"Failed to prepare Unity project: {exc}", style="red")


if __name__ == "__main__":
    app()


