"""
The sentences Whisper says into silence, and what is done about each shape of them.

The strings here are real: they are what two calls from 2026-09-03 actually came back with, and
they arrived carrying no confidence score at all — the service returns none — so nothing marked
them and they sat in the ledger looking like something somebody said.
"""

from __future__ import annotations

from vt_worker import artifacts
from vt_worker.merge import Segment, Speaker, Word


def seg(text: str, words: list[str] | None = None) -> Segment:
    made = [
        Word(start=float(i), end=float(i) + 0.5, text=(" " if i else "") + w)
        for i, w in enumerate(words or text.split())
    ]

    return Segment(speaker=Speaker.ME, start=0.0, end=float(len(made)), text=text, words=made)


# ---- recognising them --------------------------------------------------------


def test_the_sign_offs_are_recognised():
    for text in (
        "Altyazı M.K.",
        "Altyapı ve Altyapı",
        "altyazı ve altyazı",
        "İzlediğiniz için teşekkür ederim",
        "Altyazı, yorum, beğenmeyi ve kanalıma abone olmayı unutmayın.",
    ):
        assert artifacts.is_artefact(text), text


def test_repeating_one_of_them_is_still_one_of_them():
    """"Altyapı ve Altyapı Altyapı ve Altyapı" — the model saying it twice does not make it speech."""
    assert artifacts.is_artefact("Altyapı ve Altyapı Altyapı ve Altyapı")


def test_ordinary_speech_is_not_touched():
    """
    The list is short and literal on purpose. A general "does this look invented" rule eventually
    throws away something a person said, and these are words that do occur in real conversation.
    """
    for text in (
        "Altyapı çalışmaları ne durumda?",
        "Teşekkür ederim abi, sağ ol.",
        "Abone olduk mu o servise?",
        "Bu bize ne zarar verdiyse nasıl geri alacağız?",
    ):
        assert not artifacts.is_artefact(text), text


def test_nothing_is_not_an_artefact():
    assert not artifacts.is_artefact("")
    assert not artifacts.is_artefact("   ")


# ---- unsticking them from real speech ----------------------------------------


def test_a_sign_off_glued_to_the_front_of_speech_is_removed():
    """
    Every quote in the ledger is verbatim and is played back against the audio, so
    "Altyapı ve Altyapı 4 bin dolar var" attributed to a person misquotes somebody who said
    "4 bin dolar var". Leaving it in is the error, not taking it out.
    """
    assert artifacts.strip_artefact(
        "Altyapı ve Altyapı 4 bin dolar, 5 bin dolar para var lan.") == "4 bin dolar, 5 bin dolar para var lan."

    assert artifacts.strip_artefact(
        "İzlediğiniz için teşekkür ederim. Bu zaten yasak.") == "Bu zaten yasak."


def test_a_line_that_is_only_the_sign_off_is_left_alone_here():
    """Stripping would empty it. Marking is the other function's job."""
    assert artifacts.strip_artefact("Altyapı ve Altyapı") == "Altyapı ve Altyapı"


def test_a_match_in_the_middle_is_left_where_it_is():
    """
    These attach at the start of a decoding window — the model opens on silence, produces its
    sign-off, then hears somebody speak. A match in the middle of a sentence is far more likely to
    be coincidence, and cutting there would remove real words.
    """
    text = "Bize altyapı ve altyapı lazım diyorlar."

    assert artifacts.strip_artefact(text) == text


# ---- what happens to a transcript --------------------------------------------


def test_a_pure_sign_off_is_doubted_rather_than_deleted():
    """
    The rule this project follows: a machine may doubt a line and may not silently remove one. A
    transcript with a gap nobody can account for is worse than one with a line marked unreliable.
    """
    line = seg("Altyapı ve Altyapı")

    artifacts.clean([line])

    assert line.text == "Altyapı ve Altyapı"
    assert line.is_low_confidence


def test_speech_behind_a_sign_off_survives_with_its_words_realigned():
    line = seg("Altyapı ve Altyapı 4 bin dolar var")

    artifacts.clean([line])

    assert line.text == "4 bin dolar var"

    # The words have to follow the text or every timestamp after the cut points at the wrong
    # moment — which is the one thing this application must never do to a quote.
    assert [w.text.strip() for w in line.words] == ["4", "bin", "dolar", "var"]


def test_a_clean_transcript_comes_back_unchanged():
    lines = [seg("Bu bize ne zarar verdiyse nasıl geri alacağız?"), seg("Evet, aynen.")]
    before = [(s.text, len(s.words), s.is_low_confidence) for s in lines]

    artifacts.clean(lines)

    assert [(s.text, len(s.words), s.is_low_confidence) for s in lines] == before
