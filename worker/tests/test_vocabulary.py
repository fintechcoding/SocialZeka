"""The user's vocabulary and language choice must reach the engines unchanged."""

from __future__ import annotations

from vt_worker.engines.base import EngineOptions
from vt_worker.engines.faster_whisper_engine import transcribe_kwargs


def test_vocabulary_becomes_hotwords_and_an_initial_prompt():
    options = EngineOptions(model_ref="x", hotwords="Sumsub, KYC", initial_prompt="Sumsub, KYC.")

    kwargs = transcribe_kwargs(options)

    assert kwargs["hotwords"] == "Sumsub, KYC"
    assert kwargs["initial_prompt"] == "Sumsub, KYC."
    assert kwargs["language"] == "tr"
    assert "multilingual" not in kwargs


def test_no_vocabulary_sends_no_prompt():
    kwargs = transcribe_kwargs(EngineOptions(model_ref="x"))

    assert "hotwords" not in kwargs
    assert "initial_prompt" not in kwargs


def test_a_mixed_language_call_leaves_the_language_open():
    kwargs = transcribe_kwargs(EngineOptions(model_ref="x", language="tr", multilingual=True))

    assert kwargs["multilingual"] is True
    assert kwargs["language"] is None
