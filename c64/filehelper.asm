;==============================================================================
; File helper
;
; Lets programs started from the browser use files on the server with OPEN on
; device 8, the way programs use SEQ files (and PRG/USR files, and the "$"
; directory) on a real drive, and its command channel:
;
;   OPEN 2,8,2,"NAME,S,R" : INPUT#2,A$ : CLOSE 2          read
;   OPEN 2,8,2,"NAME,S,W" : PRINT#2,A$ : CLOSE 2          write (",A": append)
;   OPEN 15,8,15,"S0:NAME" : INPUT#15,E,E$,T,S : CLOSE 15  command, drive status
;
; One file at a time, plus the command channel. Files the server does not have
; (and the save channel, secondary address 1) go to the real drive as usual.
;
; The server sends the helper for disks with SEQ or USR files and for programs
; that OPEN files, and puts it on free pages it looks for in the programs. It
; relocates the code by comparing two builds (at $1000 and $1100), so it has
; to start at a page. The starter "LOADs" it through the LOAD helper (file
; name $00) and calls its last 3 bytes: JMP install.
;
;   GET  /f/{folder}/{chunk}/{name}   read:  $00, last chunk? (0/1), up to 64 bytes
;                                     write: only $02 (opened for writing); not found: only $01
;   POST /a/{folder}/{part}/{name}    up to 64 bytes written (part: bit 15 on the last one, at CLOSE)
;   GET  /c/{folder}/{command}        a command for channel 15: $00 and the drive status, e.g. "00, OK,00,00"
;
; Numbers are hex, names and commands PETSCII in hex. The server's files look
; like screen files (device 3) to the KERNAL: it keeps them in its file table,
; and CHKIN/CHKOUT/CLRCHN/CLOSE do no serial I/O.
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
V_CHKOUT        = 6
V_CLRCHN        = 8
V_CHRIN         = 10
V_CHROUT        = 12
V_STOP          = 14
V_GETIN         = 16
V_CLALL         = 18

helper_url      = $0204                 ; the LOAD helper's URL: "http://server/l/folder/"
prefix_length   = $0337                 ; ... and its length

WIC64_HTTP_GET       = $01
WIC64_HTTP_POST_URL  = $28
WIC64_HTTP_POST_DATA = $2b

SCREEN          = 3                     ; the KERNAL's device for the server's files: no I/O, input and output
CHUNK           = 64                    ; bytes per request, the server sends the same
MAX_NAME        = 20                    ; longer file names go to the real drive
MAX_COMMAND     = 30                    ; longer commands are cut off
STATUS_SIZE     = 40                    ; drive status text, with its RETURN
URL_SIZE        = 106                   ; "http://" + 30 address characters + "/l/ffff/" + a command in hex

* = ORIGIN

; The vectors as they were before (the KERNAL's), at the start of the page: JMP ($xxff) would fail
kernal:         !fill 20, 0

hooks:
    !word open, close, chkin, chkout, clrchn, chrin, chrout, stop, getin, clall

stop:   jmp (kernal+V_STOP)

;------------------------------------------------------------------------------
; OPEN
;------------------------------------------------------------------------------

open:
    lda DEVICE
    cmp #8
    bne kernal_open
    lda SECONDARY
    and #$0f
    cmp #15
    beq open_command
    cmp #1                              ; save channel
    beq kernal_open
    lda file_open                       ; one file at a time
    bne kernal_open
    lda FILENAME_LENGTH
    beq kernal_open
    cmp #MAX_NAME+1
    bcc open_file
kernal_open:
    jmp (kernal+V_OPEN)

open_file:
    ldy #0
-   lda (FILENAME),y
    sta name,y
    iny
    cpy FILENAME_LENGTH
    bne -
    sty name_length
    lda #0
    sta chunk
    sta chunk+1
    sta part
    sta part+1
    sta write_length
    jsr read_chunk                      ; A: $00 read, $02 write, otherwise not on the server
    bcs kernal_open
    cmp #2
    beq +
    cmp #0
    bne kernal_open
+   sta file_mode
    jsr register
    bcs +                               ; the KERNAL's error, e.g. file already open
    lda LFN
    sta file_lfn
    lda #1
    sta file_open
    lda #0
    sta STATUS
    clc
+   rts

open_command:
    lda command_open
    bne kernal_open
    jsr register
    bcs ++
    lda LFN
    sta command_lfn
    lda #1
    sta command_open
    jsr status_ok
    ldy #0                              ; a command in the name: run it
-   cpy FILENAME_LENGTH
    beq +
    cpy #MAX_COMMAND
    beq +
    lda (FILENAME),y
    sta command,y
    iny
    bne -
+   sty command_length
    jsr run_command
    lda #0
    sta STATUS
    clc
++  rts

; The KERNAL enters the file in its tables as a screen file. Carry set: its error.
register:
    lda #SCREEN
    sta DEVICE
    jsr kernal_open
    ldx #8                              ; keeps the carry
    stx DEVICE
    rts

;------------------------------------------------------------------------------
; CLOSE, CHKIN, CHKOUT, CLRCHN, CLALL
;------------------------------------------------------------------------------

close:
    pha                                 ; A = the logical file
    cmp file_lfn
    bne +
    ldx file_open
    beq +
    ldx file_mode
    beq ++
    jsr save_y
    jsr flush_last                      ; written: the last part, then the server saves the file
    jsr restore_y
++  lda #0
    sta file_open
    sta reading
    sta writing
+   pla
    pha
    cmp command_lfn
    bne +
    ldx command_open
    beq +
    jsr save_y
    jsr run_command                     ; a command still without its RETURN
    jsr restore_y
    lda #0
    sta command_open
    sta command_reading
    sta command_writing
+   pla
    jmp (kernal+V_CLOSE)

chkin:
    lda #0
    sta reading
    sta command_reading
    cpx file_lfn
    bne +
    lda file_open
    beq +
    lda file_mode                       ; only a file opened for reading
    bne +
    lda #1
    sta reading
+   cpx command_lfn
    bne +
    lda command_open
    sta command_reading
+   jmp (kernal+V_CHKIN)

chkout:
    lda #0
    sta writing
    sta command_writing
    cpx file_lfn
    bne +
    lda file_open
    beq +
    lda file_mode                       ; only a file opened for writing
    beq +
    lda #1
    sta writing
+   cpx command_lfn
    bne +
    lda command_open
    sta command_writing
+   jmp (kernal+V_CHKOUT)

clrchn:
    lda #0
    sta reading
    sta writing
    sta command_reading
    sta command_writing
    jmp (kernal+V_CLRCHN)

clall:
    lda #0
    sta reading
    sta writing
    sta command_reading
    sta command_writing
    sta file_open
    sta command_open
    jmp (kernal+V_CLALL)

;------------------------------------------------------------------------------
; CHRIN, GETIN, CHROUT
;------------------------------------------------------------------------------

chrin:
    lda reading
    bne read_byte
    lda command_reading
    bne read_status
    jmp (kernal+V_CHRIN)

getin:
    lda reading
    bne read_byte
    lda command_reading
    bne read_status
    jmp (kernal+V_GETIN)

; The next character of the drive status. After its RETURN (with EOI) the status is "00, OK" again.
read_status:
    stx saved_x
    ldx status_pos
    lda status_text,x
    inx
    stx status_pos
    cpx status_length
    bcc +
    pha
    sty saved_y
    jsr status_ok
    ldy saved_y
    lda STATUS
    ora #$40
    sta STATUS
    pla
+   ldx saved_x
    clc
    rts

; The next byte of the file in A (X and Y kept). The last one sets bit 6 of the status, like EOI
; from a drive; reading on returns RETURN with status $42.
read_byte:
    stx saved_x
    sty saved_y
    ldx buffer_pos
    cpx buffer_length
    bcc ++
    lda last_chunk
    bne at_end
    inc chunk
    bne +
    inc chunk+1
+   jsr read_chunk
    bcs at_end
    cmp #0
    bne at_end
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
    ldx saved_x
    ldy saved_y
    clc
    rts

at_end:
    lda STATUS
    ora #$42
    sta STATUS
    lda #$0d
    bne done

chrout:
    pha
    lda writing
    bne write_byte
    lda command_writing
    bne write_command
    pla
    jmp (kernal+V_CHROUT)

; A byte for the file: 64 bytes go to the server at once (A, X and Y kept)
write_byte:
    pla
    stx saved_x
    sty saved_y
    ldx write_length
    sta buffer,x
    inx
    stx write_length
    cpx #CHUNK
    bne +
    pha
    jsr flush
    pla
+   ldx saved_x
    ldy saved_y
    clc
    rts

; A character of a command: RETURN runs it (A, X and Y kept)
write_command:
    pla
    stx saved_x
    sty saved_y
    cmp #$0d
    beq +
    ldx command_length
    cpx #MAX_COMMAND
    bcs ++
    sta command,x
    inc command_length
    bcc ++
+   pha
    jsr run_command
    pla
++  ldx saved_x
    ldy saved_y
    clc
    rts

save_y:
    sty saved_y
    rts

restore_y:
    ldy saved_y
    rts

;------------------------------------------------------------------------------
; Requests
;------------------------------------------------------------------------------

; GET /f/: chunk "chunk" of the file into the buffer. Carry set: WiC64 error; A = the server's status.
read_chunk:
    lda #'f'
    jsr url_begin
    lda chunk+1
    jsr add_hex
    lda chunk
    jsr add_hex
    jsr add_slash
    jsr add_name
    lda #<response
    ldy #>response
    ldx #CHUNK+2
    jsr get
    bcs +
    pha
    lda response+1
    sta last_chunk
    lda #0
    sta buffer_pos
    txa
    sbc #1                              ; stored - 2 (carry is clear): the data
    bcs ++
    lda #0
++  sta buffer_length
    pla
    clc
+   rts

; POST /a/: the written bytes, as the next part (flush_last: the last one, at CLOSE)
flush_last:
    lda #$80
    bne +
flush:
    lda #0
+   ora part+1
    pha
    lda #'a'
    jsr url_begin
    pla
    jsr add_hex
    lda part
    jsr add_hex
    jsr add_slash
    jsr add_name
    lda write_length                    ; nothing written since the last part: a GET (no empty POST)
    bne +
    lda #<response
    ldy #>response
    ldx #0
    jsr get
    jmp ++
+   lda #<url
    sta source
    lda #>url
    sta source+1
    lda url_length
    sta count
    lda #0
    sta count+1
    sta limit
    lda #WIC64_HTTP_POST_URL
    jsr request
    bcs ++
    lda #<buffer
    sta source
    lda #>buffer
    sta source+1
    lda write_length
    sta count
    lda #0
    sta count+1
    lda #WIC64_HTTP_POST_DATA
    jsr request
++  inc part
    bne +
    inc part+1
+   lda #0
    sta write_length
    rts

; GET /c/: runs the command (if any); the drive status comes back
run_command:
    lda command_length
    beq ++
    lda #'c'
    jsr url_begin
    ldy #0
-   lda command,y
    jsr add_hex
    iny
    cpy command_length
    bne -
    lda #<status_response
    ldy #>status_response
    ldx #STATUS_SIZE+1
    jsr get
    bcs +
    cmp #0
    bne +
    dex                                 ; without the status byte
    beq +
    stx status_length
    lda #0
    sta status_pos
+   lda #0
    sta command_length
++  rts

; "00, OK,00,00" as the drive status
status_ok:
    ldx #status_ok_end - status_ok_text - 1
-   lda status_ok_text,x
    sta status_text,x
    dex
    bpl -
    lda #status_ok_end - status_ok_text
    sta status_length
    lda #0
    sta status_pos
    rts

; HTTP GET of the URL (X = its end) into A/Y, at most X bytes. Carry set: WiC64 error; A = first byte,
; X = bytes stored.
get:
    sta target
    sty target+1
    stx limit
    lda #<url
    sta source
    lda #>url
    sta source+1
    lda url_length
    sta count
    lda #0
    sta count+1
    lda #WIC64_HTTP_GET

; Sends a request: header with command A and length "count", then "count" bytes from "source". Receives the
; response into "target", at most "limit" bytes. Carry set: WiC64 error; A = its first byte, X = bytes stored.
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

    jsr receive                         ; response header: status, size
    sta wic64_status
    jsr receive
    sta count
    jsr receive
    sta count+1
    lda #0
    sta first_byte
    ldx #0
    ldy #0                              ; Y = 0: the first byte is still to come
-   lda count
    ora count+1
    beq +
    jsr receive
    cpy #0
    bne ++
    sta first_byte
    iny
++  cpx limit
    bcs ++
target = * + 1
    sta $ffff,x
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

receive:
    jsr wait
    lda $dd01
    rts

;------------------------------------------------------------------------------
; URL
;------------------------------------------------------------------------------

; The LOAD helper's URL with the letter A instead of its "l"; X = its end
url_begin:
    ldx url_letter
    sta url,x
    ldx url_start
    rts

add_slash:
    lda #'/'
    sta url,x
    inx
    stx url_length
    rts

; The file name in hex, and the URL's length
add_name:
    ldy #0
-   cpy name_length
    beq +
    lda name,y
    jsr add_hex
    iny
    bne -
+   stx url_length
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
    stx url_length
    rts

;------------------------------------------------------------------------------
; Data
;------------------------------------------------------------------------------

status_ok_text: !text "00, OK,00,00", $0d
status_ok_end:

file_open:      !byte 0
file_mode:      !byte 0                 ; 0 = reading, 2 = writing
file_lfn:       !byte 0
reading:        !byte 0                 ; the file is the input channel
writing:        !byte 0                 ; ... the output channel
command_open:   !byte 0
command_lfn:    !byte 0
command_reading: !byte 0
command_writing: !byte 0
chunk:          !word 0
part:           !word 0
last_chunk:     !byte 0
buffer_pos:     !byte 0
buffer_length:  !byte 0
write_length:   !byte 0
status_pos:     !byte 0
status_length:  !byte 0
command_length: !byte 0
name_length:    !byte 0
count:          !word 0
limit:          !byte 0
wic64_status:   !byte 0
first_byte:     !byte 0
saved_x:        !byte 0
saved_y:        !byte 0
url_letter:     !byte 0                 ; position of the LOAD helper's "l" in the URL
url_start:      !byte 0                 ; end of the prefix "http://server/l/folder/"
url_length:     !byte 0
header:         !byte "R", 0, 0, 0      ; WiC64 request header: command, length

name:           !fill MAX_NAME, 0
command:        !fill MAX_COMMAND, 0
status_response: !byte 0                ; the server's status, then the drive status
status_text:    !fill STATUS_SIZE, 0
response:       !byte 0, 0              ; the server's status, last chunk?
buffer:         !fill CHUNK, 0          ; the data of a chunk, or the bytes written
url:            !fill URL_SIZE, 0

;------------------------------------------------------------------------------
; Install: called once by the starter, before the program starts
;------------------------------------------------------------------------------

install:
    ldx prefix_length
    cpx #URL_SIZE - 2*MAX_COMMAND + 1
    bcs ++                              ; too long: no file helper
    stx url_start
    dex
-   lda helper_url,x
    sta url,x
    dex
    bpl -
    lda url_start
    sec
    sbc #7                              ; "/l/ffff/": the l is 7 characters from the end
    sta url_letter
    jsr status_ok

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
