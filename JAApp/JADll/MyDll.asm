; =================================================================================================
;  Projekt:       Filtr uœredniaj¹cy 5x5 dla obrazów RGB
;  Autor:         Mateusz Skrzypiec
;  Data:          Semestr zimowy 2024/2025
;  Wersja:        1.0
;
;  Historia zmian:
;    - v1.0: Implementacja podstawowego filtru uœredniaj¹cego 5x5 w jêzyku ASM (64-bit, MASM).
;
;  Opis:
;    Ten kod realizuje filtr uœredniaj¹cy 5x5 na obrazach RGB. Obraz jest przechowywany
;    jako sekwencja trójek bajtów (R, G, B) na piksel. Dla ka¿dego piksela w wierszach
;    od startY do endY obliczana jest suma wartoœci pikseli z okna 5x5 (z uwzglêdnieniem
;    brzegów obrazu) i dzielona przez 25 poprzez mno¿enie przez sta³¹ 0.04.
;    Uzyskany wynik jest konwertowany na liczbê ca³kowit¹ i przycinany (saturacja) do zakresu
;    [0..255]. Efekt zapisywany jest bezpoœrednio w tym samym buforze, nadpisuj¹c oryginalne dane.
;
;    Pseudokod (dla piksela (x, y)):
;      - Zsumuj (R, G, B) w otoczeniu 5x5,
;      - Mno¿ sumê przez 1/25 (sta³a 0.04),
;      - Zaokr¹gl i przytnij do [0..255],
;      - Zapisz z powrotem.
;    Piksele, które wypadaj¹ poza obszar obrazu, s¹ pomijane, by unikn¹æ wyjœcia poza pamiêæ.
;
;  Parametry wejœciowe i ich zakres:
;    1) rcx = pixelData  (wskaŸnik na bufor pikseli) 
;       - Bufor musi zawieraæ co najmniej (width * imageHeight * 3) bajtów.
;    2) rdx = width      (szerokoœæ obrazu, w pikselach)
;       - Zak³adany zakres: width > 0.
;    3)  r8 = startY     (pierwszy wiersz do przetwarzania)
;    4)  r9 = endY       (ostatni wiersz do przetwarzania)
;    Dodatkowo wysokoœæ obrazu (imageHeight) pobierana jest ze stosu (w tym kodzie z [rsp+104]).
;       - Zak³adany zakres: 0 <= startY < endY <= imageHeight
;         oraz imageHeight > 0.
;
;  Parametr wyjœciowy:
;    - Ten sam bufor (pixelData) jest modyfikowany w miejscu.
;      Wynikowy kana³ R, G i B ka¿dego piksela jest przycinany do [0..255].
;
;  Rejestry:
;    - Nieulotne (callee-saved): rbx, rbp, rsi, rdi, r12, r13, r14, r15 (odk³adane na stos).
;    - Lotne (caller-saved):     rax, rcx, rdx, r8, r9, r10, r11,
;                                xmm0..xmm5 do obliczeñ SSE.
;
;  Flagi:
;    - Instrukcje porównania (cmp) i skoku warunkowego (jcc) modyfikuj¹ odpowiednie flagi,
;      takie jak ZF, CF, itp. Instrukcje SSE (addps, mulps, cvtdq2ps, cvttps2dq itd.)
;      nie modyfikuj¹ flag CPU.
; =================================================================================================

.DATA
    align 16
; -------------------------------------------
; Sta³a const_0_04 (wektor [0.04, 0.04, 0.04, 0.04]):
;   - Odpowiada wartoœci 1/25 i s³u¿y do wyznaczenia œredniej.
; -------------------------------------------
const_0_04  dd 0.04, 0.04, 0.04, 0.04  

.CODE
PUBLIC ApplyASMFilter

; -------------------------------------------
; Funkcja `ApplyASMFilter` — implementacja filtru 5x5 w miejscu (in-place)
;
; Parametry (wejœciowe):
;   - rcx (pixelData): wskaŸnik na bufor (co najmniej width * height * 3 bajtów).
;   - rdx (width): szerokoœæ obrazu (width > 0).
;   - r8  (startY): pierwszy wiersz do przetwarzania (0 <= startY < imageHeight).
;   - r9  (endY): ostatni wiersz do przetwarzania   (startY < endY <= imageHeight).
;   - [rsp+104]: wysokoœæ obrazu (imageHeight), zak³adane > 0.
;
; Parametr wyjœciowy:
;   - Zmodyfikowany bufor pixelData w zakresie [0..255] dla ka¿dego kana³u.
;
; Rejestry zmieniane:
;   - RAX, RCX, RDX, R8, R9, R10, R11 (caller-saved) 
;   - XMM0..XMM5 (SSE obliczenia).
;   - Flagi CPU: zmieniane przez instrukcje cmp/jcc.
; -------------------------------------------
ApplyASMFilter PROC
    ; Zabezpieczenie rejestrów nieulotnych
    push    rbp
    push    rbx
    push    rsi
    push    rdi
    push    r12
    push    r13
    push    r14
    push    r15

    ; Pobranie niektórych parametrów ze stosu/rejestrów
    mov     r10d, [rsp + 104]   ; Wczytanie wysokoœci obrazu (imageHeight).
    mov     r12d, r8d           ; Przekazanie startY do r12d.
    mov     r13d, edx           ; Przekazanie width do r13d.
    mov     rbp, rcx            ; Zapis wskaŸnika do bufora (pixelData) w rbp.
    mov     r9d, r9d            ; Zabezpieczenie endY w r9d (no-op, ale mo¿e byæ przydatne).

; --------------------------
; Pêtla po wierszach (row_loop)
; --------------------------
row_loop:
    cmp     r12d, r9d
    jge     end_function        ; Jeœli r12d >= r9d -> koniec.

    xor     r14d, r14d          ; Zerowanie indeksu kolumn (r14d = 0).

; --------------------------
; Pêtla po kolumnach (col_loop)
; --------------------------
col_loop:
    cmp     r14d, r13d
    jge     next_row            ; Jeœli x >= width -> kolejny wiersz.

    ; Zerowanie akumulatora sumy (xmm0)
    pxor    xmm0, xmm0

    ; Rozpoczêcie skanowania okna 5x5
    mov     r15d, -2            ; r15d = pionowy offset (-2)

outer_5x5_loop:
    mov     edx, r12d
    add     edx, r15d           ; Bie¿¹cy wiersz = (y + offset)
    cmp     edx, 0
    jl      skip_row            ; Jeœli < 0 -> poza obrazem, pomijamy
    cmp     edx, r10d
    jge     skip_row            ; Jeœli >= wysokoœæ -> pomijamy

    ; Poziomy offset -2
    mov     r8d, -2

inner_5x5_loop:
    mov     eax, r14d
    add     eax, r8d            ; Bie¿¹ca kolumna = (x + offset)
    cmp     eax, 0
    jl      skip_col            ; Jeœli < 0 -> poza obrazem, pomijamy
    cmp     eax, r13d
    jge     skip_col            ; Jeœli >= width -> poza obrazem, pomijamy

    ; Obliczanie adresu piksela: (y_offset * width + x_offset) * 3
    mov     ecx, edx
    imul    ecx, r13d
    add     ecx, eax
    imul    ecx, 3

    ; Wczytanie piksela (B, G, R) do xmm4
    movd    xmm4, dword ptr [rbp + rcx]

    ; Rozszerzenie (8-bit -> 32-bit) i konwersja do float
    pxor    xmm5, xmm5
    punpcklbw xmm4, xmm5
    punpcklwd xmm4, xmm5
    cvtdq2ps xmm4, xmm4

    ; Dodanie do akumulatora (xmm0)
    addps   xmm0, xmm4

skip_col:
    add     r8d, 1
    cmp     r8d, 2
    jle     inner_5x5_loop

skip_row:
    add     r15d, 1
    cmp     r15d, 2
    jle     outer_5x5_loop

    ; Mno¿enie przez 1/25 (sta³a const_0_04) -> wyliczenie œredniej
    mulps   xmm0, xmmword ptr [const_0_04]

    ; Konwersja float -> int i saturacja do [0..255]
    cvttps2dq xmm1, xmm0
    packusdw  xmm1, xmm1
    packuswb  xmm1, xmm1

    ; Odczyt gotowego wyniku (00RRGGBB) do ebx
    movd    ebx, xmm1

    ; Wyliczenie adresu piksela wynikowego i zapis w to samo miejsce
    mov     eax, r12d
    imul    eax, r13d
    add     eax, r14d
    imul    eax, 3
    mov     dword ptr [rbp + rax], ebx

    ; Nastêpna kolumna
    inc     r14d
    jmp     col_loop

; Nastêpny wiersz
next_row:
    inc     r12d
    jmp     row_loop

; Koniec przetwarzania
end_function:
    ; Przywrócenie rejestrów nieulotnych
    pop     r15
    pop     r14
    pop     r13
    pop     r12
    pop     rdi
    pop     rsi
    pop     rbx
    pop     rbp
    ret

ApplyASMFilter ENDP
END
