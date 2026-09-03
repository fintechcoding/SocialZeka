"""The user's vocabulary and language choice must reach the engines unchanged.

The vocabulary reaches faster-whisper one way now, as ``hotwords``, and the removal of the other
way is the thing worth guarding. ``initial_prompt`` carried the same terms and was not a stronger
version of the same idea: hotwords weights a decoding window, while a prompt is text the decoder
is told it has already produced and therefore continues. Given a comma-separated list of
capitalised terms it continued the list instead of transcribing the call.
"""

from __future__ import annotations

from vt_worker.engines.base import EngineOptions
from vt_worker.engines.faster_whisper_engine import transcribe_kwargs


def test_vocabulary_becomes_hotwords():
    options = EngineOptions(model_ref="x", hotwords="Sumsub, KYC")

    kwargs = transcribe_kwargs(options)

    assert kwargs["hotwords"] == "Sumsub, KYC"
    assert kwargs["language"] == "tr"
    assert "multilingual" not in kwargs


def test_the_vocabulary_never_becomes_decoder_context():
    """The fault this whole removal is about: terms as context rather than as weight."""
    kwargs = transcribe_kwargs(EngineOptions(model_ref="x", hotwords="Sumsub, KYC"))

    assert "initial_prompt" not in kwargs
    assert "prompt" not in kwargs


def test_no_vocabulary_sends_nothing():
    kwargs = transcribe_kwargs(EngineOptions(model_ref="x"))

    assert "hotwords" not in kwargs


def test_a_mixed_language_call_leaves_the_language_open():
    kwargs = transcribe_kwargs(EngineOptions(model_ref="x", language="tr", multilingual=True))

    assert kwargs["multilingual"] is True
    assert kwargs["language"] is None
