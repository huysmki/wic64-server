;==============================================================================
; File helper
;
; Lets programs started from the browser read files from the server with
; OPEN, CHKIN, CHRIN/GETIN and CLOSE on device 8, the way programs read SEQ
; files (and PRG/USR files, and the "$" directory) from a real drive:
;
;   OPEN 2,8,2,"NAME,S,R" : INPUT#2,A$ : CLOSE 2
;
; Only reading, one file at a time. Writing, the command channel (15) and
; files the server does not have go to the real drive as usual.
;
; The server only sends the helper for disks with SEQ or USR files, and puts
; it on free pages it looks for in the programs on the disk. It relocates the
; code by comparing two builds (at $1000 and $1100), so it has to start at a
; page. The starter "LOADs" it through the LOAD helper (file name $00) and
; calls its last 3 bytes: JMP install.
;
; The file is read in chunks of 64 bytes:
;   GET /f/{folder}/{chunk}/{name in hex}   $00, last chunk? (0/1), up to 64 bytes; or only $01
; A file on the server looks like a keyboard file (device 0) to the KERNAL,
; so it keeps its file table and CHKIN/CLRCHN/CLOSE do no serial I/O.
;==============================================================================

!cpu 6502

!ifndef ORIGIN { ORIGIN = $1000 }

STATUS          = $90
MEMSIZ          = $37                   ; top of BASIC memory
FILENAME_LENGTH = $b7
LFN             = $b8
SECONDARY       = $b9
DEVICE          = $ba
FILENAME        = $bb

VECTORS         = $031a                 ; OPEN, CLOSE, CHKIN, CHKOUT, CLRCHN, CHRIN, CHROUT, STOP, GETIN, CLALL
V_OPEN          = 0
V_CLOSE         = 2
V_CHKIN         = 4
V_CLRCHN        = 8
V_CHRIN         = 10
V_GETIN         = 16
V_CLALL         = 18

helper_url      = $0204                 ; the LOAD helper's URL: "http://server/l/folder/"
prefix_length   = $0337                 ; ... and its length

CHUNK           = 64                    ; bytes per request, the server sends the same
MAX_NAME        = 20                    ; longer file names go to the real drive
URL_SIZE        = 90                    ; "http://" + 30 address characters + "/l/ffff/" + "cccc/" + 40 hex digits

* = ORIGIN

; The vectors as they were before (the KERNAL's), at the start of the page: JMP ($xxff) would fail
kernal:         !fill 20, 0

hooks:
    !word open, close, chkin, chkout, clrchn, chrin, chrout, stop, getin, clall

chkout: jmp (kernal+6)
chrout: jmp (kernal+12)
stop:   jmp (kernal+14)

;------------------------------------------------------------------------------
; KERNAL hooks
;------------------------------------------------------------------------------

open:
    lda DEVICE
    cmp #8
    bne +
    lda SECONDARY
    and #$0f
    cmp #1                              ; save channel
    beq +
    cmp #15                             ; command channel
    beq +
    lda is_open                         ; one file at a time
    bne +
    lda FILENAME_LENGTH
    beq +
    cmp #MAX_NAME+1
    bcc open_on_server
kernal_open:
+   jmp (kernal+V_OPEN)

open_on_server:
    ; URL: prefix + "cccc/" (chunk) + name in hex
    ldx url_name
    ldy #0
-   lda (FILENAME),y
    jsr add_hex
    iny
    cpy FILENAME_LENGTH
    bne -
    stx request_length
    lda #0
    sta chunk
    sta chunk+1
    jsr fetch
    bcs kernal_open                     ; not on the server: the real drive

    lda #0                              ; the KERNAL enters it in its tables as a keyboard file
    sta DEVICE
    jsr kernal_open
    ldx #8                              ; keeps the carry
    stx DEVICE
    bcs +                               ; its error, e.g. file already open
    lda LFN
    sta open_lfn
    lda #1
    sta is_open
    lda #0
    sta STATUS
    clc
+   rts

close:
    cmp open_lfn
    bne +
    ldx #0
    stx is_open
    stx reading
+   jmp (kernal+V_CLOSE)

chkin:
    lda #0
    sta reading
    cpx open_lfn
    bne +
    lda is_open
    sta reading
+   jmp (kernal+V_CHKIN)

clrchn:
    lda #0
    sta reading
    jmp (kernal+V_CLRCHN)

clall:
    lda #0
    sta reading
    sta is_open
    jmp (kernal+V_CLALL)

chrin:
    lda reading
    bne read_byte
    jmp (kernal+V_CHRIN)

getin:
    lda reading
    bne read_byte
    jmp (kernal+V_GETIN)

; The next byte of the file in A (X and Y kept). The last one sets bit 6 of the status, like EOI
; from a drive; reading on returns RETURN with status $42.
read_byte:
    stx save_x
    sty save_y
    ldx buffer_pos
    cpx buffer_length
    bcc ++
    lda last_chunk
    bne at_end
    inc chunk
    bne +
    inc chunk+1
+   jsr fetch
    bcs at_end
    ldx #0
    cpx buffer_length
    beq at_end
++  lda buffer,x
    inx
    stx buffer_pos
    cpx buffer_length
    bne done
    ldx last_chunk
    beq done
    tax
    lda STATUS
    ora #$40
    sta STATUS
    txa
done:
    ldx save_x
    ldy save_y
    clc
    rts

at_end:
    lda STATUS
    ora #$42
    sta STATUS
    lda #$0d
    bne done

;------------------------------------------------------------------------------
; Requests
;------------------------------------------------------------------------------

; Fetches chunk "chunk" of the file into the buffer. Carry set: not found or error.
fetch:
    ldx url_chunk
    lda chunk+1
    jsr add_hex
    lda chunk
    jsr add_hex

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
    lda request_length
    clc
    adc #4                              ; + request header
    sta send_count
    ldy #0
-   lda request,y
    sta $dd01
    jsr wait
    iny
    cpy send_count
    bne -
    lda #0
    sta result                          ; not found until proven otherwise

    lda #$00                            ; userport receives
    sta $dd03
    lda $dd00
    and #$fb                            ; PA2 low: ready to receive
    sta $dd00
    jsr wait                            ; the WiC64 confirms the change of direction
    lda $dd01                           ; handshake

    jsr get                             ; response header: status, size
    tax
    jsr get
    sta count
    jsr get
    bne finish                          ; more than a chunk: not ours
    txa
    bne finish                          ; WiC64 error, e.g. no network
    lda count
    beq finish
    jsr get                             ; the server's status: 0 = found
    bne finish
    lda count
    cmp #2
    bcc finish
    jsr get
    sta last_chunk
    lda count
    sec
    sbc #2
    cmp #CHUNK+1
    bcs finish
    sta buffer_length
    ldx #0
    stx buffer_pos
-   cpx buffer_length
    beq +
    jsr get
    sta buffer,x
    inx
    bne -
+   inc result

finish:
    lda #$00                            ; userport back to input, like wic64_finalize
    sta $dd03
    lda $dd0d
    plp
    lda result
    eor #1
    lsr                                 ; carry clear when found
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

is_open:        !byte 0
reading:        !byte 0                 ; the file is the input channel
open_lfn:       !byte 0
chunk:          !word 0
last_chunk:     !byte 0
buffer_pos:     !byte 0
buffer_length:  !byte 0
count:          !byte 0
result:         !byte 0
send_count:     !byte 0
save_x:         !byte 0
save_y:         !byte 0
url_chunk:      !byte 0                 ; position of the chunk number in the URL
url_name:       !byte 0                 ; position of the file name

request:        !byte 'R', $01          ; WiC64 HTTP GET
request_length: !word 0                 ; only the low byte changes
url:            !fill URL_SIZE, 0
buffer:         !fill CHUNK, 0

;------------------------------------------------------------------------------
; Install: called once by the starter, before the program starts
;------------------------------------------------------------------------------

install:
    ; the URL: the LOAD helper's, with /f/ instead of /l/, then "cccc/"
    ldx prefix_length
    cpx #URL_SIZE - 5 - 2*MAX_NAME + 1
    bcs ++                              ; too long: no file helper
    dex
-   lda helper_url,x
    sta url,x
    dex
    bpl -
    ldx prefix_length
    lda #'f'
    sta url-7,x                         ; "/l/ffff/": the l is 7 characters from the end
    stx url_chunk
    lda #'/'
    sta url+4,x
    inx
    inx
    inx
    inx
    inx
    stx url_name

    ; hook the KERNAL
    ldx #19
-   lda VECTORS,x
    sta kernal,x
    lda hooks,x
    sta VECTORS,x
    dex
    bpl -

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
