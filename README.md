<div align="center">

# 🎮 PS Game Translator

**Oyun altyazılarını gerçek zamanlı yakala, OCR ile oku, Türkçe'ye çevir ve oyunun üstünde şeffaf overlay olarak göster.**

.NET 8 · WPF · MVVM · Windows.Graphics.Capture · PaddleOCR · OPUS-MT

</div>

---

## 📸 Ekran Görüntüleri

> Görseller `docs/screenshots/` klasöründedir.

<div align="center">

### Ana Sayfa — canlı durum, hızlı ayarlar ve istatistikler
![Ana Sayfa](docs/screenshots/home.png)

### Ayarlar — pill sekmeli, kategorilere ayrılmış
![Ayarlar](docs/screenshots/settings.png)

### Yakalama & Overlay
![Yakalama](docs/screenshots/capture.png)

</div>

---

## ✨ Özellikler

| | |
|---|---|
| 🖥️ **Gerçek zamanlı yakalama** | Windows.Graphics.Capture (GDI/PrintWindow yedeği ile) — düşük gecikmeli, JPEG boru hattı |
| 🔤 **Çoklu OCR** | PaddleOCR (varsayılan), WindowsOCR, RapidOCR, EasyOCR, OneOCR — hızlı/dengeli/hibrit profilleri |
| 🌍 **Çoklu çeviri motoru** | Yerel **OPUS-MT** (fine-tune edilebilir), DeepL, Google Translate, Gemini, Groq, Ollama, LM Studio |
| ⚡ **Hızlandırılmış** | PaddleOCR `auto_growth` bellek + CTranslate2 (int8_float16) ile ~2.5-3× daha hızlı çeviri |
| 🎯 **Oyun tanıma** | Yerel ONNX (EfficientNet-B0, ~500 IGDB oyunu) + vision LLM — **güven-eşikli hibrit** |
| 📖 **Sözlük sistemi** | Oyuna özel terim sözlükleri (Elden Ring, RDR2, Witcher 3, FF, Hogwarts…) otomatik yüklenir |
| 🪟 **Şeffaf overlay** | Ayarlanabilir yazı tipi/boyut/renk/opaklık, çok-monitör desteği, altyazı-değiştirme maskesi |
| 🎨 **Tema & Dil** | Karanlık/Açık tema, Türkçe/İngilizce arayüz |
| 🧪 **Model eğitimi** | Geliştirici modunda OPUS-MT fine-tuning + BLEU/karşılaştırma araçları |

---

## 🎯 Oyun Tanıma — Güven-Eşikli Hibrit

Pencere başlığı gerçek oyun adını vermediğinde (PS5 Remote Play, YouTube yayını vb.) oyun **görüntüden** tanınır:

```
Ekran görüntüsü
      │
      ▼
┌─────────────────────┐   güven ≥ eşik (0.45)
│  ONNX EfficientNet  │ ─────────────────────────►  Sonucu kullan (hızlı, offline)
│  (~500 IGDB oyunu)  │
└─────────────────────┘   güven < eşik
      │                          │
      ▼                          ▼
  düşük güven          ┌──────────────────────┐
                       │  Vision LLM (gemma)  │ ──►  Kullanıcı onayına sun
                       └──────────────────────┘
```

Yerel ONNX modeli önce çalışır (hızlı ve tamamen çevrimdışı). Top-1 softmax güveni eşiği geçerse doğrudan kullanılır; geçmezse — aynı-motor oyunların karıştığı belirsiz durumlar — vision modeline devredilir. Böylece model **emin olmadığında yanlış ismi güvenle göstermez**. Eşik `TranslationSettings.OnnxGameConfidenceThreshold` ile ayarlanır.

---

## 📥 İndirme (EXE)

Hazır çalıştırılabilir sürüm için **[Releases](../../releases)** sayfasına bakın. Zip'i indirip açın ve `PsGameTranslator.App.exe`'yi çalıştırın.

> **Not:** OCR ve yerel çeviri, Python sunucularını (PaddleOCR / OPUS-MT) kullanır. Tamamen çevrimdışı çeviri için Python ortamının kurulu olması gerekir (aşağıya bakın). Bulut sağlayıcılar (DeepL/Google/Gemini/Groq) için yalnızca kendi API anahtarınızı Ayarlar'a girmeniz yeterlidir — **anahtarlar yalnızca sizin cihazınızda saklanır, depoya veya EXE'ye dahil edilmez.**

---

## 🧱 Proje Yapısı

| Proje | Sorumluluk |
|---|---|
| `PsGameTranslator.App` | WPF giriş noktası, DI, MVVM shell, tüm sayfalar |
| `PsGameTranslator.Core` | Paylaşılan domain modelleri (bağımlılıksız) |
| `PsGameTranslator.Capture` | Pencere yakalama (WGC + GDI yedeği) |
| `PsGameTranslator.Ocr` | OCR servis sözleşmesi ve sağlayıcıları |
| `PsGameTranslator.Overlay` | Şeffaf overlay penceresi ve ayarları |
| `PsGameTranslator.Infrastructure` | Çeviri boru hattı, sözlük, oyun tanıma, ayarlar, loglama |
| `PsGameTranslator.Tests` | Birim testleri |

---

## 🔧 Derleme & Çalıştırma

**Gereksinimler:** Visual Studio 2022, .NET 8 SDK, .NET Desktop Development workload, Python 3.11

```powershell
dotnet restore
dotnet build PsGameTranslator.sln
```

`PsGameTranslator.App`'i başlangıç projesi yapıp `F5`.

### Python ortamı (OCR + yerel çeviri)

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r tools\ocr\requirements.txt
pip install -r tools\translation\requirements.txt
```

Uygulama OCR sunucusunu (`tools/ocr/ocr_server.py`, port 8765) ve çeviri sunucusunu
(`tools/translation/translation_server.py`, port 8770) otomatik başlatır; hata ayıklama
için elle de çalıştırılabilirler.

---

## 🔒 Gizlilik & Anahtarlar

- API anahtarları (DeepL/Google/Gemini/Groq) yalnızca çalışma anında `config/user_settings.json` dosyasında, **sizin cihazınızda** saklanır.
- Bu dosya `.gitignore` ile hariç tutulur — **depoya veya yayınlanan EXE'ye asla dahil edilmez.**
- Kaynak ağacında hiçbir gerçek API anahtarı bulunmaz (yalnızca boş varsayılanlar).

---

## 📚 Dokümantasyon

Teknik rehberler (kaynak optimizasyonu, performans teorisi, mimari desenler)
[`docs/`](docs/) klasöründedir.
