# Third-Party Notices

LiveSub itself is proprietary (see [LICENSE](LICENSE)), but it is built on and
ships with third-party components that are licensed separately by their
respective owners. Those components are **not** covered by LiveSub's license and
remain subject to their own terms.

Most of the licenses below (MIT, Apache-2.0, BSD-3-Clause, CC-BY) permit
redistribution **only if their copyright/attribution notices are preserved** —
which is what this file is for. It must ship alongside any distributed build.

---

## Bundled in the installer

### .NET runtime and libraries

| Component | License |
|---|---|
| .NET / WPF runtime (Microsoft) | MIT |
| `Microsoft.Extensions.*` (Hosting, Configuration, Logging, Options) | MIT |
| `Microsoft.Data.Sqlite` | MIT |
| `Microsoft.ML.OnnxRuntime` | MIT |
| `System.Drawing.Common` | MIT |
| `Vortice.Direct3D11`, `Vortice.DXGI` | MIT |
| `Wpf.Ui` (WPF UI) | MIT |
| `Serilog`, `Serilog.Extensions.Hosting`, `Serilog.Settings.Configuration`, `Serilog.Sinks.File` | Apache-2.0 |

### Machine learning models

| Component | License / Terms | Note |
|---|---|---|
| Game recognition model (`game_recognition_efficientnet_b0.onnx`) | BSD-3-Clause (derived work) | Fine-tuned from **EfficientNet-B0** ImageNet weights shipped by **torchvision** (BSD-3-Clause). The resulting weights are a derivative of those pretrained weights. |
| Training data — game screenshots and metadata | **IGDB API Terms of Service** | Class labels and training images were sourced via IGDB (Twitch/Amazon). IGDB's terms govern that data independently of this project. |

---

## Required at runtime (not bundled — installed separately by the user)

These run as local Python sidecar services and are **not** redistributed in the
installer; the user installs them into their own Python environment.

| Component | License |
|---|---|
| **PaddleOCR**, **PaddlePaddle** | Apache-2.0 |
| **RapidOCR** (`rapidocr-onnxruntime`) | Apache-2.0 |
| **Helsinki-NLP / OPUS-MT** translation models | **CC-BY-4.0 — attribution required** |
| **Hugging Face Transformers** | Apache-2.0 |
| **CTranslate2** | MIT |
| **FastAPI**, **Uvicorn** | MIT / BSD-3-Clause |
| **Pillow** | MIT-CMU (HPND) |

### OPUS-MT attribution (CC-BY-4.0)

> Tiedemann, J. and Thottingal, S. (2020): *OPUS-MT — Building open translation
> services for the World.* Proceedings of the 22nd Annual Conference of the
> European Association for Machine Translation (EAMT), Lisbon, Portugal.
>
> Models: https://huggingface.co/Helsinki-NLP — licensed CC-BY-4.0.

---

## Optional integrations (user-provided)

Used only if the user supplies their own credentials or installs them; not
bundled and not redistributed:

- **Ollama**, **LM Studio** — local model runners
- **DeepL API**, **Google Cloud Translation**, **Google Gemini**, **Groq** —
  cloud services governed by their own terms; the user brings their own API key

---

## Trademarks

LiveSub is an independent project and is not affiliated with, endorsed by or
sponsored by Sony Interactive Entertainment or any game publisher, developer or
platform holder. All product names, logos and trademarks are the property of
their respective owners and are used for identification purposes only.
