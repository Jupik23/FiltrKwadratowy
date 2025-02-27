; =================================================================================================
;  Projekt:       Filtr u�redniaj�cy 5x5 dla obraz�w RGB
;  Autor:         Mateusz Skrzypiec
;  Data:          Semestr zimowy 2024/2025
;  Wersja:        1.0
;
;  Historia zmian:
;    - v1.0: Implementacja podstawowego filtru u�redniaj�cego 5x5 w j�zyku ASM (64-bit, MASM).
;
;  Opis:
;    Ten kod realizuje filtr u�redniaj�cy 5x5 na obrazach RGB. Obraz jest przechowywany
;    jako sekwencja tr�jek bajt�w (R, G, B) na piksel. Dla ka�dego piksela w wierszach
;    od startY do endY obliczana jest suma warto�ci pikseli z okna 5x5 (z uwzgl�dnieniem
;    brzeg�w obrazu) i dzielona przez 25 poprzez mno�enie przez sta�� 0.04.
;    Uzyskany wynik jest konwertowany na liczb� ca�kowit� i przycinany (saturacja) do zakresu
;    [0..255]. Efekt zapisywany jest bezpo�rednio w tym samym buforze, nadpisuj�c oryginalne dane.
;
;    Pseudokod (dla piksela (x, y)):
;      - Zsumuj (R, G, B) w otoczeniu 5x5,
;      - Mno� sum� przez 1/25 (sta�a 0.04),
;      - Zaokr�gl i przytnij do [0..255],
;      - Zapisz z powrotem.
;    Piksele, kt�re wypadaj� poza obszar obrazu, s� pomijane, by unikn�� wyj�cia poza pami��.
;
;  Parametry wej�ciowe i ich zakres:
;    1) rcx = pixelData  (wska�nik na bufor pikseli) 
;       - Bufor musi zawiera� co najmniej (width * imageHeight * 3) bajt�w.
;    2) rdx = width      (szeroko�� obrazu, w pikselach)
;       - Zak�adany zakres: width > 0.
;    3)  r8 = startY     (pierwszy wiersz do przetwarzania)
;    4)  r9 = endY       (ostatni wiersz do przetwarzania)
;    Dodatkowo wysoko�� obrazu (imageHeight) pobierana jest ze stosu (w tym kodzie z [rsp+104]).
;       - Zak�adany zakres: 0 <= startY < endY <= imageHeight
;         oraz imageHeight > 0.
;
;  Parametr wyj�ciowy:
;    - Ten sam bufor (pixelData) jest modyfikowany w miejscu.
;      Wynikowy kana� R, G i B ka�dego piksela jest przycinany do [0..255].
;
;  Rejestry:
;    - Nieulotne (callee-saved): rbx, rbp, rsi, rdi, r12, r13, r14, r15 (odk�adane na stos).
;    - Lotne (caller-saved):     rax, rcx, rdx, r8, r9, r10, r11,
;                                xmm0..xmm5 do oblicze� SSE.
;
;  Flagi:
;    - Instrukcje por�wnania (cmp) i skoku warunkowego (jcc) modyfikuj� odpowiednie flagi,
;      takie jak ZF, CF, itp. Instrukcje SSE (addps, mulps, cvtdq2ps, cvttps2dq itd.)
;      nie modyfikuj� flag CPU.
; =================================================================================================

.DATA
    align 16
; -------------------------------------------
; Sta�a const_0_04 (wektor [0.04, 0.04, 0.04, 0.04]):
;   - Odpowiada warto�ci 1/25 i s�u�y do wyznaczenia �redniej.
; -------------------------------------------
const_0_04  dd 0.04, 0.04, 0.04, 0.04  

.CODE
PUBLIC ApplyASMFilter

; -------------------------------------------
; Funkcja `ApplyASMFilter` � implementacja filtru 5x5 w miejscu (in-place)
;
; Parametry (wej�ciowe):
;   - rcx (pixelData): wska�nik na bufor (co najmniej width * height * 3 bajt�w).
;   - rdx (width): szeroko�� obrazu (width > 0).
;   - r8  (startY): pierwszy wiersz do przetwarzania (0 <= startY < imageHeight).
;   - r9  (endY): ostatni wiersz do przetwarzania   (startY < endY <= imageHeight).
;   - [rsp+104]: wysoko�� obrazu (imageHeight), zak�adane > 0.
;
; Parametr wyj�ciowy:
;   - Zmodyfikowany bufor pixelData w zakresie [0..255] dla ka�dego kana�u.
;
; Rejestry zmieniane:
;   - RAX, RCX, RDX, R8, R9, R10, R11 (caller-saved) 
;   - XMM0..XMM5 (SSE obliczenia).
;   - Flagi CPU: zmieniane przez instrukcje cmp/jcc.
; -------------------------------------------
ApplyASMFilter PROC
    ; Zabezpieczenie rejestr�w nieulotnych
    push    rbp
    push    rbx
    push    rsi
    push    rdi
    push    r12
    push    r13
    push    r14
    push    r15

    ; Pobranie niekt�rych parametr�w ze stosu/rejestr�w
    mov     r10d, [rsp + 104]   ; Wczytanie wysoko�ci obrazu (imageHeight).
    mov     r12d, r8d           ; Przekazanie startY do r12d.
    mov     r13d, edx           ; Przekazanie width do r13d.
    mov     rbp, rcx            ; Zapis wska�nika do bufora (pixelData) w rbp.
    mov     r9d, r9d            ; Zabezpieczenie endY w r9d (no-op, ale mo�e by� przydatne).

; --------------------------
; P�tla po wierszach (row_loop)
; --------------------------
row_loop:
    cmp     r12d, r9d
    jge     end_function        ; Je�li r12d >= r9d -> koniec.

    xor     r14d, r14d          ; Zerowanie indeksu kolumn (r14d = 0).

; --------------------------
; P�tla po kolumnach (col_loop)
; --------------------------
col_loop:
    cmp     r14d, r13d
    jge     next_row            ; Je�li x >= width -> kolejny wiersz.

    ; Zerowanie akumulatora sumy (xmm0)
    pxor    xmm0, xmm0

    ; Rozpocz�cie skanowania okna 5x5
    mov     r15d, -2            ; r15d = pionowy offset (-2)

outer_5x5_loop:
    mov     edx, r12d
    add     edx, r15d           ; Bie��cy wiersz = (y + offset)
    cmp     edx, 0
    jl      skip_row            ; Je�li < 0 -> poza obrazem, pomijamy
    cmp     edx, r10d
    jge     skip_row            ; Je�li >= wysoko�� -> pomijamy

    ; Poziomy offset -2
    mov     r8d, -2

inner_5x5_loop:
    mov     eax, r14d
    add     eax, r8d            ; Bie��ca kolumna = (x + offset)
    cmp     eax, 0
    jl      skip_col            ; Je�li < 0 -> poza obrazem, pomijamy
    cmp     eax, r13d
    jge     skip_col            ; Je�li >= width -> poza obrazem, pomijamy

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

    ; Mno�enie przez 1/25 (sta�a const_0_04) -> wyliczenie �redniej
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

    ; Nast�pna kolumna
    inc     r14d
    jmp     col_loop

; Nast�pny wiersz
next_row:
    inc     r12d
    jmp     row_loop

; Koniec przetwarzania
end_function:
    ; Przywr�cenie rejestr�w nieulotnych
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
