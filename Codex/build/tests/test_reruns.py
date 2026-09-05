import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import reruns as rr


class FamilyTests(unittest.TestCase):
    def test_split_sid_keeps_event_type(self):
        self.assertEqual(rr.split_sid("CBE_MaddieInParis2025_Intro_Dialogue"), ("CBE_MaddieInParis", "CBE_MaddieInParis2025", "Intro_Dialogue"))
        self.assertEqual(rr.split_sid("CBE_MaddieInParis_Intro_Dialogue"), ("CBE_MaddieInParis", "CBE_MaddieInParis", "Intro_Dialogue"))
        self.assertEqual(rr.split_sid("CBE_AmeliaBoulton2024B_Slot_01")[0:2], ("CBE_AmeliaBoulton", "CBE_AmeliaBoulton2024B"))
        self.assertEqual(rr.split_sid("CBE_TheGreatEscapeB_Intro")[0], "CBE_TheGreatEscape")
        self.assertEqual(rr.split_sid("LC_Autumn_Intro")[0], "LC_Autumn")     # LC and LS are different events
        self.assertIsNone(rr.split_sid("Pool_Intro"))

    def test_run_tag_needs_a_core(self):
        self.assertEqual(rr.family_of("SE_Xmas2022"), ("Xmas", "2022"))
        self.assertEqual(rr.family_of("LS_Winter"), ("Winter", ""))


class ChangeTests(unittest.TestCase):
    def test_typo_is_cosmetic(self):
        self.assertEqual(rr.classify_change("Let's merge these boxes to make more room.", "Let's Merge these boxes to make more room."), "cosmetic")
        self.assertEqual(rr.classify_change("Here it is, dearie", "Here it is, dearie."), "cosmetic")

    def test_markup_and_quotes_are_cosmetic(self):
        self.assertEqual(rr.classify_change("Did you have a <i>fantastique</i> vacation?", "Did you have a fantastique vacation?"), "cosmetic")
        self.assertEqual(rr.classify_change("I’m fine", "I'm fine"), "cosmetic")

    def test_rewrite(self):
        self.assertEqual(rr.classify_change("You wanted to see me, Grandma?", "Grandma, I realize you never want to see the estate again."), "rewritten")


class LatestTests(unittest.TestCase):
    def test_last_non_empty(self):
        self.assertEqual(rr.latest([{"from": "25.01.03", "value": "old"}, {"from": "26.07.01", "value": None}]), "old")
        self.assertIsNone(rr.latest([]))


if __name__ == "__main__":
    unittest.main()
