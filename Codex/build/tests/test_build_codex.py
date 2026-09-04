import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import build_codex as bc

TD = {"TriggerDialogue": {"StoryDefinitionId": "Pool_Intro", "DialogItems": {"Pool_Intro_01": "x", "Pool_Intro_02": "x"},
                          "CompleteActions": [{"TriggerDialogue": {"StoryDefinitionId": "Pool_Intro2", "DialogItems": {"Pool_Intro2_01": "x"}}}]}}


class StoryDefsTests(unittest.TestCase):
    def test_nested_defs_collected_in_order(self):
        defs = bc.story_defs_from_actions([TD])
        self.assertEqual(defs["Pool_Intro"], ["Pool_Intro_01", "Pool_Intro_02"])
        self.assertEqual(defs["Pool_Intro2"], ["Pool_Intro2_01"])


class AreaTriggerTests(unittest.TestCase):
    def test_area_trigger(self):
        areas = [{"Name": "Pool", "AreaId": "Pool", "HotspotsRefs": [{"Id": "Pool1", "Description": "Clean pool",
                                                                       "CompletionActions": [TD], "AppearActions": [], "FinalizationActions": []}]}]
        t = bc.triggers_from_areas(areas)
        self.assertIn(("Pool_Intro", {"kind": "area", "area": "Pool", "areaId": "Pool", "task": "Clean pool", "hotspotId": "Pool1", "phase": "task completed"}), t)
        self.assertIn(("Pool_Intro2", {"kind": "chained", "after": "Pool_Intro", "area": "Pool", "task": "Clean pool"}), t)


class ItemTriggerTests(unittest.TestCase):
    def test_item_discovered_trigger(self):
        chains = [{"Name": "Bottles", "PrimaryChain": [{"Item": {"Name": "Water Leaf", "ItemType": "Bottle_01", "OnDiscoveredActions": [TD]}, "Count": 1}], "FallbackChain": []}]
        t = bc.triggers_from_items(chains)
        self.assertEqual(t[0], ("Pool_Intro", {"kind": "itemDiscovered", "item": "Bottle_01", "itemName": "Water Leaf", "chain": "Bottles"}))


class SpeakerTests(unittest.TestCase):
    def test_left_speaks(self):
        self.assertEqual(bc.speaker_of({"LeftCharacter": "Maddie", "LeftSpeaks": True, "LeftCharacterState": "Happy",
                                        "RightCharacter": "Grandma", "RightSpeaks": False, "RightCharacterState": "Default"}), ("Maddie", "Happy"))

    def test_nobody(self):
        self.assertEqual(bc.speaker_of({"LeftCharacter": "NoChange", "LeftSpeaks": False, "RightCharacter": "NoChange", "RightSpeaks": False}), (None, None))


class PrefixTests(unittest.TestCase):
    def test_longest_prefix_wins(self):
        self.assertEqual(bc.classify_prefix("SLBE_X_01", {"SLBE_": "solo", "S": "bad"}), "solo")

    def test_default(self):
        self.assertEqual(bc.classify_prefix("Zzz", {}), bc.DEFAULT_HINT)


class LocLookupTests(unittest.TestCase):
    def test_candidates_in_priority_order(self):
        loc = {"Dialogue_Pool_Intro_01": "area text", "SP_X_01": "event text"}
        self.assertEqual(bc.loc_text(loc, "Pool_Intro_01", None), "area text")
        self.assertEqual(bc.loc_text(loc, "SP_X_01", "SP_X_01"), "event text")
        self.assertEqual(bc.loc_text({"DialogText_Maddie_07_New": "old"}, "MapFromMansionToGarage_02", "DialogText_Maddie_07_New"), "old")
        self.assertIsNone(bc.loc_text(loc, "Nope_01", None))


if __name__ == "__main__":
    unittest.main()
