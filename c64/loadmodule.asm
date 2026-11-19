;==============================================================================
; LOAD module: the LOAD helper, out of the way
;
; The LOAD helper (c64/loadhelper.asm) lives in $02a7-$02ff and the tape
; buffer, with its request in $0200. Programs that use that memory themselves
; overwrite it, and their LOADs hang. For those programs the server sends this
; module: the same LOAD from the server, but relocated to free pages like the
; file helper (built at $1000 and $1100), with its own copy of the URL. It
; takes over the LOAD vector, so the helper in the tape buffer is no longer
; needed once the program runs (and neither is the LOAD guard).
;
; The starter "LOADs" it through the LOAD helper (file name $03) and calls its
; last 3 bytes: JMP install.
;
; No timeout detection: the WiC64 itself reports network errors.
;==============================================================================

!cpu 6502

!ifndef ORIGIN { ORIGIN = $1000 }

KERNAL_LOAD     = $f4a5                 ; the default LOAD routine
STATUS          = $90
VERIFY          = $93
MEMSIZ          = $37                   ; top of BASIC memory
END_ADDRESS     = $ae                   ; end of the loaded data + 1
FILENAME_LENGTH = $b7
SECONDARY       = $b9                   ; 0 = load to the address in X/Y, 1 = to the file's address
DEVICE          = $ba
FILENAME        = $bb
LOAD_ADDRESS    = $c3                   ; X/Y of the LOAD call
LOAD_VECTOR     = $0330

helper_url      = $0204                 ; the LOAD helper's URL: "http://server/l/folder/"
prefix_length   = $0337                 ; ... and its length

MAX_NAME        = 20                    ; longer file names go to the real drive
MAX_PREFIX      = 45                    ; "http://" + 30 address characters + "/l/ffff/"

* = ORIGIN

to_kernal:
    lda VERIFY                          ; not on the server: load from the real drive
    jmp KERNAL_LOAD

load:
    sta VERIFY                          ; like the KERNAL's LOAD
    lda DEVICE
    cmp #8
    bne to_kernal
    lda VERIFY
    bne to_kernal
    lda FILENAME_LENGTH
    cmp #MAX_NAME+1
    bcs to_kernal

    ; URL = prefix + file name as hex digits
    ldx url_prefix
    ldy #0
-   cpy FILENAME_LENGTH
    beq +
    lda (FILENAME),y
    pha
    lsr
    lsr
    lsr
    lsr
    jsr hex_digit
    pla
    and #$0f
    jsr hex_digit
    iny
    bne -
+   stx header+2
    txa
    clc
    adc #4                              ; + request header
    sta send_count

    php
    sei
    lda $dd0d                           ; clear the handshake flag
    lda $dd02
    ora #$04                            ; PA2 is an output
    sta $dd02
    lda $dd00
    ora #$04                            ; PA2 high: the WiC64 receives
    sta $dd00
    lda #$ff                            ; userport sends
    sta $dd03
    ldy #0
-   lda header,y
    sta $dd01
    jsr wait
    iny
    cpy send_count
    bne -

    lda #$00                            ; userport receives
    sta $dd03
    lda $dd00
    and #$fb                            ; PA2 low: ready to receive
    sta $dd00
    jsr wait                            ; the WiC64 confirms the change of direction
    lda $dd01                           ; handshake

    jsr get                             ; response header: status, size
    pha
    jsr get
    sta count
    jsr get
    sta count+1
    pla
    bne failed                          ; WiC64 error, e.g. no network
    jsr get                             ; the server's status: 0 = found
    bne failed

    jsr get                             ; load address of the file
    tax
    jsr get
    tay
    lda SECONDARY
    bne +
    ldx LOAD_ADDRESS                    ; LOAD"x",8: to the address the caller asks for
    ldy LOAD_ADDRESS+1
+   stx store+1
    sty store+2

    lda count                           ; the file size is the response size - 3
    sec
    sbc #3
    sta count
    bcs receive
    dec count+1

receive:
    lda count
    ora count+1
    beq done
    jsr get
store:
    sta $ffff
    inc store+1
    bne +
    inc store+2
+   lda count
    bne +
    dec count+1
+   dec count
    jmp receive

done:
    jsr finish
    plp
    ldx store+1
    ldy store+2
    stx END_ADDRESS
    sty END_ADDRESS+1
    lda #0
    sta STATUS
    clc
    rts

failed:
    jsr finish
    plp
    jmp to_kernal

finish:
    lda #$00                            ; userport back to input, like wic64_finalize
    sta $dd03
    lda $dd0d
    rts

get:
    jsr wait
    lda $dd01
    rts

wait:
    lda $dd0d
    and #$10
    beq wait
    rts

hex_digit:
    cmp #10
    bcc +
    adc #'A'-'0'-10-1                   ; carry is set
+   adc #'0'
    sta url,x
    inx
    rts

count:          !word 0
send_count:     !byte 0
url_prefix:     !byte 0                 ; length of the URL up to the file name
header:         !byte "R", $01, 0, 0    ; WiC64 HTTP GET, URL length; the URL follows
url:            !fill MAX_PREFIX + 2*MAX_NAME, 0

;------------------------------------------------------------------------------
; Install: called once by the starter, before the program starts
;------------------------------------------------------------------------------

install:
    ldx prefix_length
    cpx #MAX_PREFIX+1
    bcs ++                              ; too long: the LOAD helper stays
    stx url_prefix
    dex
-   lda helper_url,x
    sta url,x
    dex
    bpl -

    lda #<load
    sta LOAD_VECTOR
    lda #>load
    sta LOAD_VECTOR+1

    ; BASIC keeps its variables and strings below the module
    lda #>ORIGIN
    cmp #$a0
    bcs ++
    cmp MEMSIZ+1
    bcs ++
    sta MEMSIZ+1
    lda #0
    sta MEMSIZ
++  rts

    jmp install                         ; the entry: the last 3 bytes
