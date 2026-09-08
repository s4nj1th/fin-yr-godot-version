"""Tests the off-by-default gating and safe-fallback behavior of the optional
LLM mutation path. Deliberately makes zero real network calls — is_available()
must be correct enough that propose_mutation() short-circuits before ever
reaching requests.post, and that's asserted directly via monkeypatching
requests.post to raise if it's ever called."""

import pytest

import config
import genome
import llm_mutation


@pytest.fixture(autouse=True)
def no_network_calls_allowed(monkeypatch):
    def _fail(*args, **kwargs):
        raise AssertionError("llm_mutation must not make network calls when unavailable")

    monkeypatch.setattr(llm_mutation.requests, "post", _fail)


class TestIsAvailable:
    def test_off_mode_is_never_available_even_with_a_key(self, monkeypatch):
        monkeypatch.setattr(config, "LLM_MUTATION_MODE", "off")
        monkeypatch.setattr(config, "LLM_API_KEY", "some-key")
        assert llm_mutation.is_available() is False

    def test_auto_mode_unavailable_without_a_key(self, monkeypatch):
        monkeypatch.setattr(config, "LLM_MUTATION_MODE", "auto")
        monkeypatch.setattr(config, "LLM_PROVIDER", "anthropic")
        monkeypatch.setattr(config, "LLM_API_KEY", "")
        assert llm_mutation.is_available() is False

    def test_auto_mode_available_once_a_key_is_configured(self, monkeypatch):
        monkeypatch.setattr(config, "LLM_MUTATION_MODE", "auto")
        monkeypatch.setattr(config, "LLM_PROVIDER", "anthropic")
        monkeypatch.setattr(config, "LLM_API_KEY", "sk-test")
        assert llm_mutation.is_available() is True

    def test_local_provider_available_without_any_key(self, monkeypatch):
        monkeypatch.setattr(config, "LLM_MUTATION_MODE", "auto")
        monkeypatch.setattr(config, "LLM_PROVIDER", "local")
        monkeypatch.setattr(config, "LLM_API_KEY", "")
        assert llm_mutation.is_available() is True

    def test_default_repo_configuration_is_unavailable(self, monkeypatch):
        """A fresh checkout with default config must never call an LLM — this
        is what keeps the core evolutionary loop LLM-free by construction."""
        monkeypatch.setattr(config, "LLM_MUTATION_MODE", "auto")
        monkeypatch.setattr(config, "LLM_PROVIDER", "anthropic")
        monkeypatch.setattr(config, "LLM_API_KEY", "")
        assert config.LLM_API_KEY == ""
        assert llm_mutation.is_available() is False


class TestProposeMutationFallback:
    def test_returns_none_without_calling_the_network_when_unavailable(self, monkeypatch):
        monkeypatch.setattr(config, "LLM_MUTATION_MODE", "off")
        parent = genome.make_default_genome()
        result = llm_mutation.propose_mutation(parent, recent_outcomes=[])
        assert result is None
