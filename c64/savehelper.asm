;==============================================================================
; Save helper
;
; Lets programs started from the browser SAVE to the server on device 8: into
; the folder or disk image they came from, like a real drive. The memory goes
; to the server with the WiC64's HTTP POST, in parts of up to 32 KB:
;
;   POST /w/{folder}/{start address}/{part}/{name in hex}   -> $00 (saved), or $01
;
; part: 2 hex digits, bit 7 set on the last part. When the server can't save
; the file (or the WiC64 reports an error), the SAVE goes to the real drive.
;
; The server only sends the helper to programs that SAVE (the BASIC keyword or
; a call of the KERNAL's SAVE), relocated to free pages like the file helper
; (built at $1000 and $1100). The starter "LOADs" it through the LOAD helper
; (file name $02) and calls its last 3 bytes: JMP install.
;==============================================================================

!cpu 6502

!ifndef ORIGIN { ORIGIN = $1000 }

STATUS          = $90
MEMSIZ          = $37                   ; top of BASIC memory
END_ADDRESS     = $ae                   ; end of the memory to save + 1 (set by the KERNAL's SAVE)
FILENAME_LENGTH = $b7
DEVICE          = $ba
FILENAME        = $bb
START_ADDRESS   = $c1                   ; start of the memory to save (set by the KERNAL's SAVE)
SAVE_VECTOR     = $0332

helper_url      = $0204                 ; the LOAD helper's URL: "http://server/l/folder/"
prefix_length   = $0337                 ; ... and its length

WIC64_HTTP_POST_URL  = $28
WIC64_HTTP_POST_DATA = $2b

MAX_NAME        = 20                    ; longer file names go to the real drive
URL_SIZE        = 93                    ; "http://" + 30 address characters + "/l/ffff/" + "ssss/pp/" + 40 hex digits

* = ORIGIN

; The vector as it was before (the KERNAL's), at the start of the page: JMP ($xxff) would fail
kernal_save:    !word 0

save:
    lda DEVICE
    cmp #8
    bne to_kernal
    lda FILENAME_LENGTH
    beq to_kernal
    cmp #MAX_NAME+1
    bcs to_kernal

    sec                                 ; the number of bytes: end - start
    lda END_ADDRESS
    sbc START_ADDRESS
    sta remaining
    lda END_ADDRESS+1
    sbc START_ADDRESS+1
    sta remaining+1
    bcc to_kernal                       ; end before start
    ora remaining
    bne save_on_server                  ; nothing to save: the real drive
to_kernal:
    jmp (kernal_save)

save_on_server:
    ; URL: prefix + "ssss/pp/" (start address, part) + name in hex
    ldx url_start
    lda START_ADDRESS+1
    jsr add_hex
    lda START_ADDRESS
    jsr add_hex
    ldx url_name
    ldy #0
-   lda (FILENAME),y
    jsr add_hex
    iny
    cpy FILENAME_LENGTH
    bne -
    stx url_length

    lda START_ADDRESS
    sta data_pointer
    lda START_ADDRESS+1
    sta data_pointer+1
    lda #0
    sta part

next_part:
    ; up to 32 KB: is this the last part?
    lda remaining+1
    cmp #$80
    bcc last_part
    bne more_parts
    lda remaining
    bne more_parts
last_part:
    lda remaining
    sta part_length
    lda remaining+1
    sta part_length+1
    lda #$80
    bne +
more_parts:
    lda #0
    sta part_length
    lda #$80
    sta part_length+1
    lda #0
+   ora part
    ldx url_part
    jsr add_hex

    ; the URL
    lda #<url
    sta source
    lda #>url
    sta source+1
    lda url_length
    sta count
    lda #0
    sta count+1
    lda #WIC64_HTTP_POST_URL
    jsr request
    bcs failed

    ; the data, from where the last part ended
    lda data_pointer
    sta source
    lda data_pointer+1
    sta source+1
    lda part_length
    sta count
    lda part_length+1
    sta count+1
    lda #WIC64_HTTP_POST_DATA
    jsr request
    bcs failed
    cmp #0                              ; the server's status: 0 = saved (or part received)
    bne failed
    lda source
    sta data_pointer
    lda source+1
    sta data_pointer+1

    sec
    lda remaining
    sbc part_length
    sta remaining
    lda remaining+1
    sbc part_length+1
    sta remaining+1
    inc part
    lda remaining
    ora remaining+1
    beq +
    jmp next_part

+   lda #0
    sta STATUS
    clc
    rts

failed:
    jmp (kernal_save)                   ; the real drive, if there is one

;------------------------------------------------------------------------------
; WiC64
;------------------------------------------------------------------------------

; Sends the request: header with command A and length "count", then "count" bytes from "source" (which ends
; up after them). Receives the response: carry set on a WiC64 error, A = its first byte (0 when empty).
request:
    sta header+1
    lda count
    sta header+2
    lda count+1
    sta header+3

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

    ldx #0
-   lda header,x
    jsr send
    inx
    cpx #4
    bne -

-   lda count
    ora count+1
    beq +
source = * + 1
    lda $ffff
    jsr send
    inc source
    bne ++
    inc source+1
++  lda count
    bne ++
    dec count+1
++  dec count
    jmp -

+   lda #$00                            ; userport receives
    sta $dd03
    lda $dd00
    and #$fb                            ; PA2 low: ready to receive
    sta $dd00
    jsr wait                            ; the WiC64 confirms the change of direction
    lda $dd01                           ; handshake

    jsr get                             ; response header: status, size
    sta wic64_status
    jsr get
    sta count
    jsr get
    sta count+1
    lda #0
    sta first_byte
    ldx #0                              ; X = 0: the first byte is still to come
-   lda count
    ora count+1
    beq +
    jsr get
    cpx #0
    bne ++
    sta first_byte
    inx
++  lda count
    bne ++
    dec count+1
++  dec count
    jmp -

+   lda #$00                            ; userport back to input, like wic64_finalize
    sta $dd03
    lda $dd0d
    plp
    lda wic64_status
    cmp #1                              ; carry set: WiC64 error
    lda first_byte
    rts

send:
    sta $dd01
wait:
    lda $dd0d
    and #$10
    beq wait
    rts

get:
    jsr wait
    lda $dd01
    rts

; Adds A as two hex digits to the URL at X
add_hex:
    pha
    lsr
    lsr
    lsr
    lsr
    jsr +
    pla
    and #$0f
+   cmp #10
    bcc +
    adc #'A'-'0'-10-1                   ; carry is set
+   adc #'0'
    sta url,x
    inx
    rts

;------------------------------------------------------------------------------
; Data
;------------------------------------------------------------------------------

remaining:      !word 0                 ; bytes still to send
data_pointer:   !word 0
part:           !byte 0
part_length:    !word 0
count:          !word 0
wic64_status:   !byte 0
first_byte:     !byte 0
url_start:      !byte 0                 ; position of the start address in the URL
url_part:       !byte 0                 ; ... of the part number
url_name:       !byte 0                 ; ... of the file name
url_length:     !byte 0
header:         !byte "R", 0, 0, 0      ; WiC64 request header: command, length
url:            !fill URL_SIZE, 0

;------------------------------------------------------------------------------
; Install: called once by the starter, before the program starts
;------------------------------------------------------------------------------

install:
    ; the URL: the LOAD helper's, with /w/ instead of /l/, then "ssss/pp/"
    ldx prefix_length
    cpx #URL_SIZE - 8 - 2*MAX_NAME + 1
    bcs ++                              ; too long: no save helper
    dex
-   lda helper_url,x
    sta url,x
    dex
    bpl -
    ldx prefix_length
    lda #'w'
    sta url-7,x                         ; "/l/ffff/": the l is 7 characters from the end
    stx url_start
    lda #'/'
    sta url+4,x
    sta url+7,x
    txa
    clc
    adc #5
    sta url_part
    adc #3
    sta url_name

    lda SAVE_VECTOR                     ; hook the KERNAL
    sta kernal_save
    lda SAVE_VECTOR+1
    sta kernal_save+1
    lda #<save
    sta SAVE_VECTOR
    lda #>save
    sta SAVE_VECTOR+1

    ; BASIC keeps its variables and strings below the helper
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
