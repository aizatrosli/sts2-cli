"""Tests that all engine output is English."""
import json
import re

CJK = re.compile(r"[\u3040-\u30ff\u3400-\u9fff\uac00-\ud7af]")


class TestLanguage:
    def test_card_names_are_english(self, game):
        state = game.start(seed="lang_en1")
        deck = state.get("player", {}).get("deck", [])
        assert any("Strike" in str(c.get("name", "")) for c in deck), \
            f"Expected English card names, got: {[c['name'] for c in deck[:3]]}"

    def test_output_has_no_cjk_text(self, game):
        state = game.start(seed="lang_en2")
        assert not CJK.search(json.dumps(state, ensure_ascii=False))
        state = game.skip_neow(state)
        assert not CJK.search(json.dumps(state, ensure_ascii=False))

    def test_lang_field_is_ignored(self, game):
        """Old clients may still send "lang"; the output stays English."""
        state = game.send({"cmd": "start_run", "character": "Ironclad", "seed": "lang_def1", "lang": "zh"})
        assert state["type"] == "decision", state
        assert not CJK.search(json.dumps(state, ensure_ascii=False))
