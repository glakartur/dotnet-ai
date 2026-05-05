---
date: 2026-05-02
topic: daemon-idle-auto-shutdown
---

# Daemon Idle Auto Shutdown

## Summary

Daemon ma automatycznie kończyć pracę po okresie bezczynności. Domyślny timeout to 60 minut, można go zmienić parametrem wywołania CLI dla bieżącej sesji demona, a wartość `off` ma wyłączać auto-zamykanie.

---

## Problem Frame

Obecny daemon działa do ręcznego zatrzymania lub restartu systemu, co przy dłuższych przerwach w pracy utrzymuje niepotrzebny proces i zasoby. Zespół chce zachować szybki start kolejnych wywołań, ale jednocześnie automatycznie wygaszać serwer, gdy nie jest używany.

---

## Requirements

**Idle lifecycle**
- R1. System musi automatycznie zamknąć daemon po upływie efektywnego timeoutu bezczynności od ostatniego obsłużonego wywołania.
- R2. Każde nowe wywołanie obsłużone przed upływem timeoutu musi resetować licznik bezczynności.
- R3. Domyślna wartość timeoutu musi wynosić 60 minut.

**Parametr konfiguracji sesji**
- R4. Użytkownik musi móc przekazać timeout jako parametr wywołania aplikacji.
- R5. Wartość timeoutu przekazana parametrem musi obowiązywać dla bieżącej sesji działającego demona (do kolejnej zmiany lub restartu).
- R6. Specjalna wartość `off` musi wyłączać auto-zamykanie dla bieżącej sesji demona.

**Walidacja i stabilność**
- R7. Nieprawidłowa wartość parametru timeoutu musi zwrócić jednoznaczny błąd i nie może zmienić aktualnie obowiązującego timeoutu sesji.
- R8. Auto-zamykanie musi przebiegać w sposób bezpieczny (graceful), tak aby kolejne wywołanie mogło poprawnie uruchomić nową sesję demona.

---

## Acceptance Examples

- AE1. **Covers R1, R3.** Given daemon działa z ustawieniami domyślnymi i nie ma nowych wywołań, when mija 60 minut bezczynności, daemon automatycznie się zamyka.
- AE2. **Covers R2.** Given pozostała 1 minuta do auto-zamknięcia, when użytkownik wykona nowe polecenie `dotnet aicraft`, licznik bezczynności startuje od nowa.
- AE3. **Covers R4, R5.** Given użytkownik uruchamia polecenie z parametrem timeoutu (np. 15 minut), when daemon pozostaje bezczynny, then zamyka się po 15 minutach, a nie po 60.
- AE4. **Covers R6.** Given użytkownik ustawia timeout na `off`, when daemon jest bezczynny dłużej niż 60 minut, then daemon nie zamyka się automatycznie.
- AE5. **Covers R7.** Given użytkownik poda niepoprawną wartość timeoutu, when polecenie zostanie wykonane, then system zwraca błąd walidacji i zachowuje dotychczasowy timeout sesji.
- AE6. **Covers R8.** Given daemon zakończył się z powodu bezczynności, when użytkownik wykona kolejne polecenie, then nowa sesja demona uruchamia się i odpowiada poprawnie.

---

## Success Criteria

- Daemon zamyka się automatycznie po okresie bezczynności zgodnym z efektywnym timeoutem (domyślnym lub sesyjnym).
- Użytkownik może świadomie kontrolować czas wygaszania przez parametr wywołania, w tym wyłączyć timeout wartością `off`.
- Po auto-zamknięciu kolejne wywołania CLI działają poprawnie bez ręcznej interwencji.

---

## Scope Boundaries

- Brak trwałego zapisu timeoutu do konfiguracji globalnej lub projektowej.
- Brak zmian w modelu wielu daemonów poza obecnym podejściem jedna sesja na solution.
- Brak rozbudowanego systemu polityk uprawnień, rate-limitów i zaawansowanego monitoringu dla zmian timeoutu.

---

## Key Decisions

- Timeout sesji, nie jednorazowego wywołania: pozwala użytkownikowi zmienić zachowanie demona bez konieczności ponownej konfiguracji przy każdym poleceniu.
- `off` zamiast `0` jako wyłączenie: zmniejsza ryzyko niejednoznacznej semantyki wartości liczbowych.

---

## Dependencies / Assumptions

- Semantyka "ostatnie wywołanie" liczona jest od zakończenia obsługi wywołania.
- Graceful shutdown pozostaje kompatybilny z obecnym mechanizmem uruchamiania demona przy następnym wywołaniu.
