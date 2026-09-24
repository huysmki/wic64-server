;==============================================================================
; LOAD helper
;
; The browser installs this routine in the KERNAL LOAD vector ($0330) before it
; starts a program. A LOAD from device 8 asks the server for the file first:
; the server looks in the .d64 image or folder the program was started from
; (and in the other .d64 images next to it, e.g. the B side of a game).
; Files the server does not have are loaded from the real drive as usual.
;
; It does not fit in the tape buffer alone, so it has two parts: $02a7-$02ff
; and $0334-$03fb. The server sends both, together with the request block for
; $0200 (BASIC input buffer, not used again after a LOAD): the WiC64 request
; header and the URL up to the file name. Only the file name is added here.
;
; No timeout detection: the WiC64 itself reports network errors.
;==============================================================================

!cpu 6502

KERNAL_LOAD     = $f4a5                 ; the default LOAD routine
STATUS          = $90
VERIFY          = $93
END_ADDRESS     = $ae                   ; end of the loaded data + 1
FILENAME_LENGTH = $b7
SECONDARY       = $b9                   ; 0 = load to the address in X/Y, 1 = to the file's address
DEVICE          = $ba
FILENAME        = $bb
LOAD_ADDRESS    = $c3                   ; X/Y of the LOAD call

request         = $0200                 ; "R", HTTP GET, URL length (2), URL
url             = request + 4

* = $02a7
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

not_found:
    jsr finish
    plp
kernal:
    lda VERIFY                          ; not on the server: load from the real drive
    jmp KERNAL_LOAD

hex_digit:
    cmp #10
    bcc +
    adc #'A'-'0'-10-1                   ; carry is set
+   adc #'0'
    sta url,x
    inx
    rts

get:
    jsr wait
    lda $dd01
    rts

count:          !word 0

!if * > $0300 {
    !error "first part of the LOAD helper does not fit in $02a7-$02ff"
}

* = $0334
    jmp start
prefix_length:  !byte 0                 ; +3: length of the URL before the file name, filled in by the server

start:
    sta VERIFY                          ; like the KERNAL's LOAD
    lda DEVICE
    cmp #8
    bne kernal
    lda VERIFY
    bne kernal

    ; URL = prefix + file name as hex digits
    ldx prefix_length
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
+   stx request+2
    txa
    clc
    adc #4                              ; + request header
    sta send_count+1

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
-   lda request,y
    sta $dd01
    jsr wait
    iny
send_count:
    cpy #0
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
    bcs +
    dec count+1
+   jmp receive
failed:
    jmp not_found

finish:
    lda #$00                            ; userport back to input, like wic64_finalize
    sta $dd03
    lda $dd0d
    rts

wait:
    lda $dd0d
    and #$10
    beq wait
    rts

!if * > $03fc {
    !error "LOAD helper does not fit in the tape buffer"
}
