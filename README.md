# 🧠 Spacjomat — Hybrydowy Korektor Języka Polskiego (AI + Baza 4.5M Słów Offline)

**Spacjomat** to lekki, systemowy asystent pisania dla Windows 10/11 w stylu Grammarly, działający w dowolnym komunikatorze i edytorze (Telegram, WhatsApp, przeglądarka, Discord, Word, poczta). Łączy **błyskawiczną bazę Offline (~1–8 ms)** z **inteligentną korektą AI całego sensu wypowiedzi** (Google Gemini, Groq, OpenAI lub lokalna Ollama).

---

## ⚡ Główne funkcje i skróty klawiszowe

| Skrót / Przycisk | Tryb | Czas | Jak działa |
| :--- | :--- | :--- | :--- |
| **`F7`** lub `🧠 AI (F7)` | **Tylko AI** | ~0.6–1.5 s | Czyta całe pole tekstowe, rozumie kontekst i poprawia nawet mocno przekręcone wyrazy, interpunkcję oraz gramatykę (tryb karty Grammarly z wariantami lub szybka auto-podmiana). |
| **`F8`** / **`Ctrl+Shift+K`** lub `⚡ F8` | **Tylko Baza Offline** | ~1–8 ms | Zero sieci i zero zużycia limitów API. Poprawia zaznaczony fragment (lub całe pole automatycznie) na bazie 4,5 mln form SJP, GBoard, OpenSubtitles i ponad 29 tys. reguł. |
| **`Spacja` / `.` `,` `!` `?`** | **Autokorekta na żywo** | < 1 ms | Bezpiecznie poprawia literówki i polskie znaki w trakcie pisania (`Backspace` cofa poprawkę i zapamiętuje wyjątek). |
| **`F9`** | **Pauza / Wznów** | — | Wstrzymuje lub wznawia korektę na żywo. |

---

## 🛠️ Architektura i rozwiązania inżynierskie

- **Niskopoziomowy WinAPI Hook (`WH_KEYBOARD_LL`) + `SendInput`**: atomowa podmiana tekstu w dowolnym oknie Windows z obsługą polskich znaków (`KEYEVENTF_UNICODE`) oraz automatycznym przywracaniem schowka.
- **Niekradnące fokusu pływające UI (`WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`)**:
  - **Pływająca pigułka (`GrammarlyPillForm`)**: skalowalny panel w rogu ekranu (presety wymiarów + rozciąganie myszką `◢`, przezroczystość), niewidoczny na pasku zadań.
  - **Karta podpowiedzi (`GrammarlySuggestionForm`)**: wyświetla listę błędów i warianty do wstawienia jednym kliknięciem bez odbierania fokusu aktywnej aplikacji.
  - **Edytor Studio (`SpacjomatStudioForm`)**: dwupanelowy edytor do dłuższych tekstów.
- **Kompaktowy słownik binarny (`sjp_hashes.d48`)**: **4 497 109** posortowanych 48-bitowych hashy FNV-1a zakodowanych różnicowo (varint), dzięki czemu cały słownik zajmuje tylko **17 MB** i ładuje się w ~0,6 s (wyszukiwanie binarne `O(log n)`) + korpus 221 tys. słów z ważonym algorytmem QWERTY-Levenshtein.
- **Optymalizacja sieci i licznik limitów (`AiUsageTracker`)**: wyłączone WPAD proxy, `Expect100Continue = false`, `KeepAlive`, kompresja `GZip`, pre-warm TLS przy starcie oraz panel statystyk (RPD, RPM, tokeny, błędy `429` dla każdego modelu).
- **Bezpieczny start bez `.exe`**: kompilacja kodu C# w pamięci przy starcie przez PowerShell `Add-Type` (brak niepodpisanych plików wykonywalnych blokowanych przez Windows Device Guard).

---

## 🚀 Jak pobrać i uruchomić (Windows 10/11)

1. Pobierz plik **[`2_Bez_Klucza_Portfolio.zip`](https://github.com/warszawavork/Spacjomat-/raw/main/2_Bez_Klucza_Portfolio.zip)** (ok. 17,8 MB) i rozpakuj w dowolne miejsce na dysku.
2. Kliknij dwukrotnie **`Zainstaluj_Skrot_Na_Pulpicie.bat`** (odblokuje skrypty i utworzy skrót **`Spacjomat`** na Pulpicie) lub uruchom bezpośrednio `Spacjomat.vbs`.
3. Tryb **Offline (`F8` i `Spacja`)** działa od razu bez żadnego klucza.
4. Aby włączyć **AI (`F7`)**:
   - Kliknij **`⚙`** na pigułce w prawym dolnym rogu ekranu,
   - Wklej własny darmowy klucz z [aistudio.google.com/apikey](https://aistudio.google.com/apikey) (lub wybierz preset **Groq** z [console.groq.com/keys](https://console.groq.com/keys)),
   - Kliknij **`🧪 Testuj połączenie AI`** — klucz zapisze się automatycznie.
