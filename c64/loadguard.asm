;==============================================================================
; LOAD guard
;
; The LOAD helper keeps its WiC64 request (header and URL up to the file name)
; in $0200, the BASIC input buffer. BASIC's INPUT and INPUT# overwrite it, so
; a BASIC program that INPUTs and then LOADs would send garbage. This guard
; keeps a copy and sits in front of the LOAD helper in the LOAD vector: when
; the request header is gone, it puts the copy back.
;
; A LOAD typed in direct mode has its file name in $0200 itself: then the copy
; can't go back, and the LOAD goes to the real drive.
;
; The server only sends the guard to BASIC programs that use INPUT and LOAD,
; relocated to a free page like the file helper (built at $1000 and $1100).
; The starter "LOADs" it through the LOAD helper (file name $01) and calls its
; last 3 bytes: JMP install.
;==============================================================================

!cpu 6502

!ifndef ORIGIN { ORIGIN = $1000 }

MEMSIZ          = $37                   ; top of BASIC memory
FILENAME        = $bb
LOAD_VECTOR     = $0330
KERNAL_LOAD     = $f4a5                 ; the default LOAD routine
request         = $0200                 ; the LOAD helper's request: "R", HTTP GET, URL length (2), URL
prefix_length   = $0337                 ; length of the URL up to the file name
load_helper     = $0334
COPY_SIZE       = $0259 - $0200

* = ORIGIN

load:
    pha                                 ; the verify flag
    lda request
    cmp #'R'
    bne +
    lda request+1
    cmp #$01                            ; HTTP GET
    beq ++
+   lda FILENAME+1
    cmp #>request
    beq direct_mode
copy_count:
    ldx #0                              ; set by install: length - 1
-   lda copy,x
    sta request,x
    dex
    bpl -
++  pla
    jmp load_helper

direct_mode:
    pla
    jmp KERNAL_LOAD

copy:           !fill COPY_SIZE, 0

install:
    lda prefix_length
    clc
    adc #3                              ; + 4 header bytes - 1
    sta copy_count+1
    tax
-   lda request,x
    sta copy,x
    dex
    bpl -

    lda #<load
    sta LOAD_VECTOR
    lda #>load
    sta LOAD_VECTOR+1

    ; BASIC keeps its variables and strings below the guard
    lda #>ORIGIN
    cmp #$a0
    bcs +
    cmp MEMSIZ+1
    bcs +
    sta MEMSIZ+1
    lda #0
    sta MEMSIZ
+   rts

    jmp install                         ; the entry: the last 3 bytes
