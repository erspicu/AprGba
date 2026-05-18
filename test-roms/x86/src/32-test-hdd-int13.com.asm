; 32-test-hdd-int13.com — verify INT 13h AH=08 (get drive params) for HDD.
; Output via Port 0xE9 debug.

cpu 8086        ; force NASM to use short Jcc; otherwise `jc err` to a far
                ; label generates 286+ `0F 82 disp16` which our XT CPU does
                ; not implement (will trap as unknown opcode).
bits 16
org  0x100

start:
    ; INT 13h AH=08, DL=0x80 (HDD 0)
    mov ah, 0x08
    mov dl, 0x80
    int 0x13
    jnc ok          ; short jc -> err overshoots 127 bytes; flip sense
    jmp err
ok:

    ; Save returned values
    mov [bx_save], bx
    mov [cx_save], cx
    mov [dx_save], dx
    mov [ah_save], ah

    ; Print preamble
    mov si, preamble
    call print_str

    ; Print CH (max cyl low byte)
    mov al, [cx_save+1]    ; CH = high byte of CX
    call print_byte_hex
    call print_space

    ; Print CL (sectors + cyl high bits)
    mov al, [cx_save]
    call print_byte_hex
    call print_space

    ; Print DH (max head)
    mov al, [dx_save+1]
    call print_byte_hex
    call print_space

    ; Print DL (drive count)
    mov al, [dx_save]
    call print_byte_hex
    call print_space

    ; Print BL (drive type for HDD)
    mov al, [bx_save]
    call print_byte_hex
    call print_newline

    ; Compute decoded values
    mov si, max_cyl_label
    call print_str
    ; max cyl = (CL[7:6] << 8) | CH
    mov al, [cx_save]      ; CL
    and al, 0xC0
    mov cl, 6
    shr al, cl             ; AL = bits 8-9 of cyl
    mov ah, al             ; AH = cyl high
    mov al, [cx_save+1]    ; AL = cyl low
    ; AX = max cylinder
    call print_word_hex
    call print_newline

    mov si, sect_label
    call print_str
    mov al, [cx_save]
    and al, 0x3F
    call print_byte_hex
    call print_newline

    mov si, head_label
    call print_str
    mov al, [dx_save+1]
    call print_byte_hex
    call print_newline

    mov si, count_label
    call print_str
    mov al, [dx_save]
    call print_byte_hex
    call print_newline

    mov si, done_msg
    call print_str
    ; PASS sentinel for grep
    mov si, pass_msg
    call print_str

    ; INT 21h DOS terminate
    mov ax, 0x4C00
    int 0x21

err:
    mov si, err_msg
    call print_str
    mov ax, 0x4C01
    int 0x21

print_str:
    lodsb
    test al, al
    jz .done
    out 0xE9, al
    push ax
    push dx
    mov dl, al
    mov ah, 2          ; DOS putchar
    int 0x21
    pop dx
    pop ax
    jmp print_str
.done:
    ret

print_byte_hex:
    push ax
    mov ah, al
    shr al, 1
    shr al, 1
    shr al, 1
    shr al, 1
    call hex_nibble
    pop ax
    and al, 0x0F
    call hex_nibble
    ret

print_word_hex:
    push ax
    mov al, ah
    call print_byte_hex
    pop ax
    call print_byte_hex
    ret

hex_nibble:
    cmp al, 10
    jb .digit
    add al, 'A' - 10
    out 0xE9, al
    push ax
    push dx
    mov dl, al
    mov ah, 2          ; DOS putchar
    int 0x21
    pop dx
    pop ax
    ret
.digit:
    add al, '0'
    out 0xE9, al
    push ax
    push dx
    mov dl, al
    mov ah, 2          ; DOS putchar
    int 0x21
    pop dx
    pop ax
    ret

print_space:
    mov al, ' '
    out 0xE9, al
    push ax
    push dx
    mov dl, al
    mov ah, 2          ; DOS putchar
    int 0x21
    pop dx
    pop ax
    ret

print_newline:
    mov al, 13
    out 0xE9, al
    push ax
    push dx
    mov dl, al
    mov ah, 2          ; DOS putchar
    int 0x21
    pop dx
    pop ax
    mov al, 10
    out 0xE9, al
    push ax
    push dx
    mov dl, al
    mov ah, 2          ; DOS putchar
    int 0x21
    pop dx
    pop ax
    ret

preamble db '[INT13/AH=08 DL=80] CH CL DH DL BL = ', 0
max_cyl_label db 'max_cyl=0x', 0
sect_label db 'sectors_per_track=0x', 0
head_label db 'max_head=0x', 0
count_label db 'drive_count=0x', 0
done_msg db '[TEST DONE]', 13, 10, 0
pass_msg db '[TEST_PASS]', 13, 10, 0
err_msg  db '[INT 13h FAILED]', 13, 10, 0

bx_save dw 0
cx_save dw 0
dx_save dw 0
ah_save db 0
