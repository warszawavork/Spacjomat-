# Spacjomat — Hybrydowy Korektor Języka Polskiego (AI + Baza 4.5M Słów Offline)

**Spacjomat** to lekki, systemowy asystent pisania dla Windows 10/11 w stylu Grammarly, działający w dowolnym oknie (Telegram, WhatsApp, przeglądarka, Word, Discord, poczta). Łączy **błyskawiczną bazę Offline (~1–8 ms)** z **inteligentną korektą AI całego sensu wypowiedzi** (Google Gemini, Groq, OpenAI lub lokalna Ollama).

---

## Główne funkcje i skróty klawiszowe

| Skrót / Przycisk | Tryb działania | Czas | Co robi |
| :--- | :--- | :--- | :--- |
| **`F7`** lub `🧠 AI (F7)` | **Tylko AI** | ~0.6–1.5 s | Analizuje całe pole tekstowe, rozumie kontekst i poprawia nawet mocno przekręcone wyrazy, interpunkcję oraz gramatykę (z kartą wyboru wariantów Grammarly lub natychmiastową auto-podmianą). |
| **`F8`** / **`Ctrl+Shift+K`** lub `⚡ F8` | **Tylko Baza Offline** | ~1–8 ms | Zero sieci i zero limitów API. Poprawia zaznaczony fragment (lub całe pole automatycznie, gdy nic nie zaznaczono) w oparciu o słownik 4.5M form SJP, bazę GBoard/OpenSubtitles i 29K+ reguł. |
| **`Spacja` / `.` `,` `!` `?`** | **Autokorekta na żywo** | < 1 ms | Bezpiecznie poprawia oczywiste literówki i polskie znaki w trakcie pisania (`Backspace` cofa poprawkę i uczy wyjątek). |
| **`F9`** | **Pauza / Wznów** | — | Wstrzymuje lub wznawia autokorektę na żywo. |

---

## Architektura i rozwiązania techniczne

- **Niskopoziomowy WinAPI Hook (`WH_KEYBOARD_LL`) + `SendInput`**: atomowa podmiana tekstu w dowolnej aplikacji Windows z obsługą polskich znaków (`KEYEVENTF_UNICODE`) oraz ochroną schowka.
- **Niekradnące fokusu UI (`WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`)**:
  - **Pływająca pigułka (`GrammarlyPillForm`)**: skalowalna zaokrąglona pigułka (presety + zmiana rozmiaru myszką `◢`, przezroczystość, pozycja), ukryta na pasku zadań.
  - **Karta podpowiedzi (`GrammarlySuggestionForm`)**: wyświetla listę wykrytych błędów i warianty do wstawienia jednym kliknięciem bez utraty fokusu aktywnego komunikatora.
  - **Edytor Studio (`SpacjomatStudioForm`)**: dwupanelowy edytor do korekty długich wiadomości i dokumentów.
- **Szybki słownik binarny (`sjp_hashes.bin`)**: 4 497 109 posortowanych 64-bitowych hashy FNV-1a ładowanych w ~35 ms (wyszukiwanie binarne $O(\log n)$) + korpus 221K słów z ważonym algorytmem QWERTY-Levenshtein.
- **Optymalizacja sieci i licznik limitów (`AiUsageTracker`)**: wyłączone WPAD proxy, `Expect100Continue = false`, `KeepAlive`, kompresja `GZip`, automatyczne rozgrzewanie TLS oraz panel statystyk (RPD, RPM, tokeny, błędy `429` per model).
- **Bezpieczny start pod Device Guard / AppLocker**: kompilacja kodu C# w pamięci przy starcie przez PowerShell `Add-Type` — brak niepodpisanych plików `.exe`.

---

## Szybkie uruchomienie (dla użytkowników)

1. Rozpakuj folder w dowolne miejsce na dysku (np. `Pulpit\Spacjomat`).
2. Kliknij dwukrotnie **`Zainstaluj_Skrot_Na_Pulpicie.bat`** (odblokuje skrypty i utworzy ikonę `Spacjomat` na Pulpicie) **lub** uruchom bezpośrednio `Spacjomat.vbs`.
3. Skróty **`F8`** i korekta po **`Spacji`** działają od razu bez żadnego klucza.
4. Aby włączyć AI pod **`F7`**:
   - Kliknij **`⚙`** na pigułce w rogu ekranu,
   - Wklej darmowy klucz z [aistudio.google.com/apikey](https://aistudio.google.com/apikey) (lub wybierz preset **Groq** z [console.groq.com/keys](https://console.groq.com/keys)),
   - Kliknij **`🧪 Testuj połączenie AI`** (udany test automatycznie zapisuje i aktywuje klucz).
