import asyncio
import importlib.util
import json
import sys
from pathlib import Path

mode = sys.argv[1]
setup_path = Path(sys.argv[2])
output_path = Path(sys.argv[3])
server_path = Path.cwd() / "server.py"
spec = importlib.util.spec_from_file_location("spire_lens_mcp_server", server_path)
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(data):
    output_path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def parse_tool_json(text, tool_name):
    try:
        data = json.loads(text)
    except Exception as exc:
        raise RuntimeError(f"{tool_name} returned non-JSON: {text[:500]}") from exc
    if data.get("status") == "error":
        raise RuntimeError(f"{tool_name} failed: {data}")
    return data


def normalize_setup_id(value, prefix):
    text = str(value or "").strip().upper()
    marker = f"{prefix}."
    return text[len(marker):] if text.startswith(marker) else text


def iter_visible_cards(state):
    player = state.get("player") if isinstance(state, dict) else {}
    if not isinstance(player, dict):
        return
    for pile in ("hand", "draw_pile", "discard_pile", "exhaust_pile"):
        cards = player.get(pile)
        if not isinstance(cards, list):
            continue
        for index, card in enumerate(cards):
            if isinstance(card, dict):
                yield pile, index, card


def assert_loaded_state_matches_setup(setup, state):
    if not isinstance(state, dict):
        raise RuntimeError("Loaded game state was not a JSON object.")

    state_type = state.get("state_type")
    # state_type IS the live room type, lowercased (McpMod.StateBuilder:
    # combatRoom.RoomType.ToString().ToLower() -> "monster" | "elite" | "boss").
    # required_room_type is the deterministic gate for room-type-conditional
    # behavior (e.g. a relic that only fires "at the start of Elite combats"):
    # the harness enters that room and the scenario fails here unless the live
    # combat actually is that type. next_normal_encounter only selects WHICH
    # Monster encounter is fought and can never produce an Elite/Boss room, so
    # it must not be used to stage one.
    required_room_type = str(setup.get("required_room_type") or "").strip().lower()
    if required_room_type:
        if state_type != required_room_type:
            raise RuntimeError(
                f"Scenario declared required_room_type={setup.get('required_room_type')!r} "
                f"but loaded state_type={state_type!r}. A room-type-gated effect cannot be "
                f"proven in the wrong room type."
            )
    elif setup.get("next_normal_encounter") and state_type != "monster":
        raise RuntimeError(
            f"Scenario declared next_normal_encounter={setup.get('next_normal_encounter')!r} "
            f"but loaded state_type={state_type!r}, not monster."
        )

    deprecated_cards = []
    for pile, index, card in iter_visible_cards(state):
        name = str(card.get("name") or "")
        description = str(card.get("description") or "")
        if name.lower() == "deprecated card" or "card was removed in a recent update" in description.lower():
            deprecated_cards.append({"pile": pile, "index": index, "name": name, "description": description})
    if deprecated_cards:
        raise RuntimeError(
            "Scenario loaded Deprecated Card placeholders, which means the scenario deck contains invalid card ids: "
            + json.dumps(deprecated_cards, ensure_ascii=False)
        )

    player = state.get("player") if isinstance(state.get("player"), dict) else {}
    relics = player.get("relics") if isinstance(player.get("relics"), list) else []
    present_relic_ids = {
        normalize_setup_id(relic.get("id"), "RELIC")
        for relic in relics
        if isinstance(relic, dict)
    }
    expected_relics = []
    if isinstance(setup.get("relics"), list):
        expected_relics.extend(setup.get("relics") or [])
    if isinstance(setup.get("add_relics"), list):
        expected_relics.extend(setup.get("add_relics") or [])
    for relic_id in expected_relics:
        normalized = normalize_setup_id(relic_id, "RELIC")
        if normalized and normalized not in present_relic_ids:
            raise RuntimeError(
                f"Scenario expected relic {relic_id!r}, but loaded relic ids were {sorted(present_relic_ids)}."
            )

    removed_relics = setup.get("remove_relics") if isinstance(setup.get("remove_relics"), list) else []
    for relic_id in removed_relics:
        normalized = normalize_setup_id(relic_id, "RELIC")
        if normalized and normalized in present_relic_ids:
            raise RuntimeError(f"Scenario removed relic {relic_id!r}, but it is still present after load.")

    return {
        "status": "ok",
        "state_type": state_type,
        "deprecated_card_count": len(deprecated_cards),
        "present_relic_ids": sorted(present_relic_ids),
    }


async def materialize_only():
    setup = read_json(setup_path)
    kwargs = {
        "base_name": setup["base_save_name"],
        "scenario_name": setup["scenario_name"],
        "deck": setup.get("deck"),
        "add_cards": setup.get("add_cards"),
        "remove_cards": setup.get("remove_cards"),
        "relics": setup.get("relics"),
        "add_relics": setup.get("add_relics"),
        "remove_relics": setup.get("remove_relics"),
        "gold": setup.get("gold"),
        "current_hp": setup.get("current_hp"),
        "max_hp": setup.get("max_hp"),
        "max_energy": setup.get("max_energy"),
        "next_normal_encounter": setup.get("next_normal_encounter"),
    }
    materialized = parse_tool_json(await server.materialize_scenario_save(**kwargs), "materialize_scenario_save")
    write_json({
        "status": "pass",
        "mode": mode,
        "scenario_setup": setup,
        "materialized": materialized,
    })


async def install_only():
    existing = read_json(output_path) if output_path.exists() else {"status": "pass"}
    setup = read_json(setup_path)
    installed = parse_tool_json(await server.install_save_as_current(setup["scenario_name"], "scenario"), "install_save_as_current")
    existing["status"] = "pass"
    existing["mode"] = mode
    existing["scenario_setup"] = setup
    existing["installed"] = installed
    write_json(existing)


async def materialize_install():
    await materialize_only()
    existing = read_json(output_path)
    setup = read_json(setup_path)
    installed = parse_tool_json(await server.install_save_as_current(setup["scenario_name"], "scenario"), "install_save_as_current")
    existing.update({
        "installed": installed,
    })
    write_json(existing)


async def settle(predicate, timeout_seconds, since_version=-1):
    """The first settled game state matching `predicate`, or — on timeout — the
    latest state, which the post-load assertion then judges."""
    try:
        return await server._wait_for_state(predicate, timeout_seconds, since_version=since_version)
    except TimeoutError:
        return parse_tool_json(await server.get_game_state("json"), "get_game_state")


async def validate_load():
    existing = read_json(output_path) if output_path.exists() else {"status": "pass"}
    setup = read_json(setup_path)
    # Launching through Steam can leave the remote mirror populated while the
    # AppData working save is absent. Install again after the bridge is ready so
    # the in-game saved-run loader and validator see the same current_run.save.
    live_installed = parse_tool_json(await server.install_save_as_current(setup["scenario_name"], "scenario"), "install_save_as_current")
    validate = parse_tool_json(await server.validate_current_run_save(), "validate_current_run_save")
    # Loading before STS2 finishes its startup (logo, LaunchMainMenu) cancels
    # that startup and shows the generic startup-error popup even though combat
    # loads. The bridge counts as settled only once NGame.GameStartupComplete
    # has finished, so a settled menu state is safe to load from. Every wait
    # below blocks in the bridge until the game state changes; nothing polls.
    try:
        menu_state = await server._wait_for_state(lambda s: s.get("state_type") == "menu", 30)
    except TimeoutError as exc:
        raise RuntimeError(f"Game did not reach a settled menu state before loading scenario save: {exc}") from exc
    loaded = parse_tool_json(await server.load_current_run_save(), "load_current_run_save")
    state = await settle(
        lambda s: s.get("state_type") not in (None, "menu", "unknown", "loading")
        and (s.get("state_type") != "monster" or bool((s.get("battle") or {}).get("enemies"))),
        20,
        since_version=int(loaded.get("state_version", -1)),
    )
    # Deterministic room-type staging. A loaded save settles into whatever room
    # the run was on (often a map or a normal Monster combat). Room-type-gated
    # behavior — e.g. Booming Conch's "at the start of Elite combats" — can only
    # be exercised in that room type, and next_normal_encounter cannot create
    # one (it only selects which Monster encounter is fought). So when the
    # scenario declares required_room_type, the HARNESS enters that room via the
    # already-exposed enter_debug_room tool and then re-settles the live combat.
    # The post-load assertion fails the scenario unless the live state_type
    # actually matches, so a room-type-gated test can never silently run in the
    # wrong room and rationalize a verdict.
    required_room_type = str(setup.get("required_room_type") or "").strip()
    room_entry = None
    if required_room_type:
        room_entry = parse_tool_json(await server.enter_debug_room(required_room_type), "enter_debug_room")
        want = required_room_type.lower()
        state = await settle(
            lambda s: s.get("state_type") == want and bool((s.get("battle") or {}).get("enemies")),
            20,
        )
    existing["required_room_type"] = required_room_type or None
    existing["required_room_entry"] = room_entry
    existing["live_installed"] = live_installed
    existing["validated"] = validate
    existing["pre_load_menu_state"] = menu_state
    existing["loaded"] = loaded
    existing["mode"] = mode
    existing["game_state"] = state
    existing["state_type"] = state.get("state_type") if state else None
    existing["loaded_character_id"] = (state or {}).get("character_id") or (state or {}).get("player", {}).get("character_id")
    existing["scenario_state_validation"] = assert_loaded_state_matches_setup(setup, state)
    write_json(existing)


if mode == "materialize_install":
    asyncio.run(materialize_install())
elif mode == "materialize_only":
    asyncio.run(materialize_only())
elif mode == "install_only":
    asyncio.run(install_only())
elif mode == "validate_load":
    asyncio.run(validate_load())
else:
    raise SystemExit(f"unknown mode: {mode}")
