# baratrum

**Local-first AI companion with real-time voice, 250K context memory, and zero filters.**

Run it on your own hardware. Own your data. Talk to something that actually remembers you.

---

## What is baratrum?

baratrum is a self-hosted AI companion designed for people who want full control over their interactions. It features a **250,000-token context window**, **real-time speech-to-text** (Qwen3-ASR), **instant voice response** (OmniVoice + Fish Audio S2 Pro), and **no content filters**.

You host the model. You host the voice pipeline. Your conversations never leave your machine unless you want them to.

---

## Features

- **🧠 250K Context Window** — Long-term memory that actually persists across sessions.
- **🎙️ Real-Time Voice** — Streaming STT and TTS with sub-second latency. Talk naturally; it talks back.
- **🏠 Local-First** — Can run entirely offline on your own GPU. No API keys, no rate limits, no remote logging.

---

## Architecture

| Component | Technology |
|-----------|------------|
| LLM Inference | Gemma 4 (250K context) via vLLM / llama.cpp |
| Speech-to-Text | Qwen3-ASR |
| Text-to-Speech | OmniVoice |
| Backend | .NET / C# |
| Frontend | Vue.js 3 + Quasar |
| Mobile | Swift with webview (iOS) |
| Containerization | Docker + Docker Compose |
| GPU | CUDA (NVIDIA) or MLX (Apple Silicon) |

---

## Hardware Requirements

### Minimum (Text-Only)
- **GPU:** 12 GB VRAM
- **Storage:** 20 GB

### Recommended (Full Voice + Text)
- **GPU:** 24 GB VRAM (RTX 4090 / RTX 5090)
- **RAM:** 32 GB
- **Storage:** 50 GB (models included)

### Apple Silicon
- M3 Max or better recommended for full voice pipeline.
- Uses MLX backend for efficient inference.

---

## Quick Start


### 1. Clone the repository
```bash
git clone https://github.com/erenakyildiz-code/PublicBaratrumBackend.git
cd PublicBaratrumBackend
```
### 2. Run 
```bash
docker compose up -d
```

## License

baratrum is licensed under the **Business Source License 1.1 (BSL 1.1)**.

- **Source code is fully available.**
- **You can run it locally for personal or internal business use for free.**
- **You cannot offer baratrum as a hosted/embedded paid service competing with our official paid offering without a commercial license.**

Four years after each release, that version transitions to the **GNU General Public License v2.0**.

See [LICENSE](LICENSE) for the full text.

---

## Community

- **Discord:** [discord.gg/baratrum](https://discord.gg/ATPAwfWaK) — Support, and dev talk.
- **Issues:** Use GitHub Issues for bugs and feature requests.

We do not use filters, and we do not moderate community content beyond illegal material. If you are looking for a sanitized corporate experience, this is not it.

---

## Self-Hosting vs. Hosted

| | Self-Hosted (You) | Hosted by Us |
|---|---|---|
| **Cost** | Free (electricity only) | ~$0.05 per message |
| **Privacy** | Absolute | I can see |
| **Hardware** | Your GPU required | None needed |
| **Customization** | Full source access | Limited |

If you want to support the project but lack the hardware, use the hosted option. If you have an RTX 5090 and a grudge against cloud AI, run it locally.

---

## Acknowledgments

- [Qwen](https://github.com/QwenLM/Qwen) for the base model and ASR pipeline
- [Fish Audio](https://github.com/fishaudio) for S2 Pro TTS
- [OmniVoice](https://github.com/omnivoice) for the voice streaming layer

---

**Built by Eren Akyıldız in Turkey. Run locally. Talk freely.**
