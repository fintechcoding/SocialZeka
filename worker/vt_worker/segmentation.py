"""Re-cut transcript segments so their boundaries match the pauses in the audio.

Why this is needed:

Whisper decides segment boundaries from its own decoding, not from the audio timeline, and it
gets this wrong in both directions.

  Too coarse: when the VAD filter removes silence, two utterances six seconds apart look
  continuous to the model, producing a segment stamped 0.00-13.04 whose text starts with a
  greeting and ends with a price quoted eleven seconds later.

  Too fine: the model also ends segments mid-sentence, so one spoken turn arrives as
  "We agreed on 12,000" followed by "for the whole batch. Is that still correct?".

That is fatal for this application specifically. Every commitment, price and red flag surfaced
by the analysis layer has to carry a timestamp the user can click to hear the moment for
themselves. A quote anchored eleven seconds from where it was said destroys exactly the
verification step the whole design rests on, and a quote chopped in half loses the meaning.

The word-level timestamps stay accurate even when the segment boundaries do not, so this splits
on the pauses those words reveal and then rejoins the pieces that belong to one turn.
"""

from __future__ import annotations

from vt_worker.merge import Segment

# Conversational speech routinely pauses for most of a second inside a single sentence — the
# reference recording has a 0.84 s gap between "morning." and "I". Real turn boundaries are
# several seconds. Splitting above 1.5 s separates turns without cutting sentences in half.
DEFAULT_MAX_GAP = 1.5


def resegment_on_gaps(segments: list[Segment], max_gap: float = DEFAULT_MAX_GAP) -> list[Segment]:
    """Rebuild segment boundaries so that one segment is one spoken turn.

    Split first, then merge. Doing it in that order means boundaries invented by the decoder are
    discarded entirely and the result depends only on where the speaker actually paused.
    """
    if max_gap <= 0:
        return segments

    return _merge_adjacent(_split_on_gaps(segments, max_gap), max_gap)


def _split_on_gaps(segments: list[Segment], max_gap: float) -> list[Segment]:
    """Split each segment wherever consecutive words are more than ``max_gap`` apart.

    Segments without word timestamps are passed through untouched: there is nothing to split on,
    and inventing boundaries would be worse than keeping the original.
    """
    out: list[Segment] = []

    for segment in segments:
        if len(segment.words) < 2:
            out.append(segment)
            continue

        runs: list[list] = [[segment.words[0]]]

        for word in segment.words[1:]:
            if word.start - runs[-1][-1].end > max_gap:
                runs.append([word])
            else:
                runs[-1].append(word)

        if len(runs) == 1:
            # No pause to split on. Keep the original segment rather than rebuilding its text
            # from the word list: an engine whose words do not cover the text in full would
            # otherwise silently lose part of the transcript.
            out.append(segment)
            continue

        out.extend(_from_words(segment, run) for run in runs)

    return out


def _merge_adjacent(segments: list[Segment], max_gap: float) -> list[Segment]:
    """Join consecutive segments from the same speaker separated by less than ``max_gap``.

    This undoes the decoder ending a segment mid-sentence. Only neighbours are considered and
    only when no real pause separates them, so distinct turns are never glued together.
    """
    if not segments:
        return []

    out: list[Segment] = [segments[0]]

    for segment in segments[1:]:
        previous = out[-1]

        same_speaker = segment.speaker == previous.speaker
        close_enough = segment.start - previous.end <= max_gap

        # Without word timestamps a merged segment could not be split again, so leave those alone.
        have_words = bool(previous.words) and bool(segment.words)

        if same_speaker and close_enough and have_words:
            out[-1] = _joined(previous, segment)
        else:
            out.append(segment)

    return out


def _joined(first: Segment, second: Segment) -> Segment:
    text = f"{first.text.strip()} {second.text.strip()}".strip()

    return Segment(
        speaker=first.speaker,
        start=first.start,
        end=second.end,
        text=text,
        # Keep the less confident of the two. Confidence gates whether numbers from this segment
        # may feed automatic contradiction detection, so the cautious value is the correct one.
        avg_logprob=_min_optional(first.avg_logprob, second.avg_logprob),
        no_speech_prob=_max_optional(first.no_speech_prob, second.no_speech_prob),
        words=[*first.words, *second.words],
    )


def _min_optional(a: float | None, b: float | None) -> float | None:
    values = [v for v in (a, b) if v is not None]
    return min(values) if values else None


def _max_optional(a: float | None, b: float | None) -> float | None:
    values = [v for v in (a, b) if v is not None]
    return max(values) if values else None


def _from_words(parent: Segment, words: list) -> Segment:
    """Build a segment covering exactly the span of ``words``."""
    return Segment(
        speaker=parent.speaker,
        start=float(words[0].start),
        end=float(words[-1].end),
        text="".join(w.text for w in words).strip(),
        # Confidence was measured over the parent decode, so it carries over unchanged. It is a
        # coarse "is this audio trustworthy" gate, not a per-word probability.
        avg_logprob=parent.avg_logprob,
        no_speech_prob=parent.no_speech_prob,
        words=list(words),
    )
