import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import render_md as rm

CODEX = {
    "versions": ["26.02.01", "26.07.01"],
    "characters": {"Maddie": {"name": "Maddie"}},
    "lines": {
        "Pool_Intro_01": {"text": [{"from": "26.02.01", "value": "Hi!"}, {"from": "26.07.01", "value": "Hello!"}],
                          "speaker": [{"from": "26.02.01", "value": "Maddie"}], "state": [{"from": "26.02.01", "value": "Happy"}],
                          "seen": {"first": "26.02.01", "last": "26.07.01"}},
        "Pool_Intro_02": {"text": [{"from": "26.02.01", "value": None}], "speaker": [{"from": "26.02.01", "value": None}],
                          "state": [{"from": "26.02.01", "value": None}], "seen": {"first": "26.02.01", "last": "26.07.01"}},
    },
    "stories": {"Pool_Intro": {"lines": [{"from": "26.02.01", "value": ["Pool_Intro_01", "Pool_Intro_02"]}],
                               "triggers": [{"kind": "area", "area": "Pool", "task": "Clean pool", "phase": "task completed", "from": "26.02.01", "to": "26.07.01"}],
                               "seen": {"first": "26.02.01", "last": "26.07.01"}}},
    "items": {}, "tasks": {}, "slides": {}, "events": {}, "gaps": {"unknownTriggerStories": [], "referencedWithoutLines": [], "locMissing": []},
}


class StoryMdTests(unittest.TestCase):
    def test_story_markdown(self):
        md = rm.story_md("Pool_Intro", CODEX["stories"]["Pool_Intro"], CODEX)
        self.assertIn("### Pool: Clean pool", md)
        self.assertIn("**MADDIE** (Happy): Hello!", md)
        self.assertIn("~~Hi!~~", md)           # previous wording shown
        self.assertIn("26.07.01", md)          # version of the change
        self.assertNotIn("Pool_Intro_02:", md)  # silent line not rendered as text

    def test_slug(self):
        self.assertEqual(rm.slug("Deb's Room / Attic"), "Deb-s-Room-Attic")


if __name__ == "__main__":
    unittest.main()
