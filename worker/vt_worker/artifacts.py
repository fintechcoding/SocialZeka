"""
The sentences Whisper says when nobody is speaking.

Every Whisper model was trained on subtitle files scraped from video, and a great many of those
files end the same way: a sign-off, a request to subscribe, a credit for whoever wrote the
subtitles. Those phrases are therefore what the model has learned to associate with the end of
audio and with audio containing no speech — so when a decoding window opens on silence, that is
what comes out. It is not a mishearing of anything; the sound it is transcribing is not there.

This application produces more of that silence than most: the two sides of a call are recorded
separately, so each channel is quiet for as long as the other person is talking, and one channel
is *supposed* to be quiet for most of a conversation.

**Why this list exists here rather than being left to the service.** stt.ex5.ai runs a
hallucination filter and reports what it removed, and these get past it — measured on two calls
from 2026-09-03, which came back with nine and five of them. They arrive with no confidence score
attached, because that service returns none, so nothing downstream marks them: they enter the
ledger looking exactly like something somebody said.

**What is done about them, and what is deliberately not.**

A line that is *only* one of these is marked uncertain rather than deleted. The rule this project
follows is that a machine may doubt a line and may not silently remove one — a transcript with a
gap nobody can account for is worse than a transcript with a line marked unreliable.

A line where one of these is stuck *in front of* real speech has the artefact removed and the
speech kept. That is not the same decision. Every quote in the ledger is verbatim and is played
back against the audio, so "Altyapı ve Altyapı 4 bin dolar var" attributed to a person is a
misquotation of somebody who said "4 bin dolar var" — leaving it in is the error, not removing it.

The list is deliberately short and literal. A general "does this look like a hallucination" rule
would eventually throw away something a person said, and the phrases below are ones no one says
in a phone call.
"""

from __future__ import annotations

import re
import unicodedata

# The families, as they actually arrive. Written in full rather than as patterns because a
# too-clever regular expression is how "abone" starts matching a conversation about subscriptions.
#
# "Altyapı" is not a typo for "Altyazı" here — it is what the model produces, having half-heard its
# own training data, and both spellings turn up.
_PHRASES = [
    "altyazı m.k.",
    "altyazı m .k.",
    "altyapı m.k.",
    "altyazı ve altyazı",
    "altyapı ve altyapı",
    "izlediğiniz için teşekkür ederim",
    "izlediğiniz için teşekkürler",
    "abone olmayı unutmayın",
    "kanalıma abone olmayı unutmayın",
    "yorum beğenmeyi ve kanalıma abone olmayı unutmayın",
    "altyazı yorum beğenmeyi ve kanalıma abone olmayı unutmayın",
    "bir sonraki videoda görüşmek üzere",
    "bizi izlediğiniz için teşekkür ederiz",
]

# How much of a line has to be artefact before the whole line is treated as one. A sign-off with
# three real words after it is a real line with a sign-off on the front; a sign-off with one
# stray syllable after it is a sign-off.
_MOSTLY = 0.75


def _fold(text: str) -> str:
    """Lower case without the Turkish dotted-i trap, punctuation flattened, spaces collapsed."""
    text = text.replace("I", "ı").replace("İ", "i").lower()
    text = unicodedata.normalize("NFKC", text)
    text = re.sub(r"[^\w\s]", " ", text, flags=re.UNICODE)

    return re.sub(r"\s+", " ", text).strip()


_FOLDED = sorted((_fold(p) for p in _PHRASES), key=len, reverse=True)


def is_artefact(text: str) -> bool:
    """Whether this line is one of the sign-offs and nothing else."""
    folded = _fold(text)
    if not folded:
        return False

    for phrase in _FOLDED:
        if not folded.startswith(phrase):
            continue

        # The model says these on a loop — "Altyapı ve Altyapı Altyapı ve Altyapı" is one real
        # line from this archive. Every repetition is consumed before asking how much is left,
        # or a line made entirely of the phrase measures as only half phrase.
        rest = folded
        while rest.startswith(phrase):
            rest = rest[len(phrase):].strip()

        if not rest or len(rest) / len(folded) <= (1 - _MOSTLY):
            return True

    return False


def strip_artefact(text: str) -> str:
    """
    The line without the sign-off glued to its front, or unchanged when there is none.

    Only from the front, and only when something is left. These attach at the start of a decoding
    window — the model opens on silence, produces its sign-off, then hears somebody begin to speak
    — so a match in the middle of a sentence is far more likely to be a coincidence than an
    artefact, and cutting there would take out real speech.
    """
    stripped = text.strip()
    folded = _fold(stripped)

    for phrase in _FOLDED:
        if not folded.startswith(phrase):
            continue

        # Walk the original string until as many folded characters have been consumed as the
        # phrase holds, so the cut lands on the original's own punctuation and spacing.
        consumed = 0
        for index, _ in enumerate(stripped):
            if _fold(stripped[: index + 1]) == phrase:
                consumed = index + 1
                break

        if not consumed:
            continue

        rest = stripped[consumed:].lstrip(" ,.;:-–—")
        if rest:
            return rest

    return stripped


def clean(segments):
    """
    Marks the sign-offs and unglues the ones stuck to real speech.

    Returns the same list. Nothing is dropped: a line that is nothing but an artefact stays, with
    ``no_speech_prob`` set high enough that ``Segment.is_low_confidence`` reports it — so it is
    visible in the transcript, excluded from the automatic contradiction checks, and still there
    for somebody to play back and judge.
    """
    for segment in segments:
        if is_artefact(segment.text):
            # Above the 0.6 threshold the local engine's own scores are judged against. Not
            # certainty that it is noise — a number saying this line is not evidence.
            segment.no_speech_prob = max(segment.no_speech_prob or 0.0, 0.95)
            continue

        shorter = strip_artefact(segment.text)
        if shorter != segment.text:
            segment.text = shorter

            # The words are gone from the text, so the word list must lose them too or the two
            # disagree and every timestamp after the cut points at the wrong moment.
            if segment.words:
                keep = len(shorter.split())
                segment.words = segment.words[-keep:] if keep else []

    return segments
