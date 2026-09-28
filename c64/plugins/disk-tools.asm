;==============================================================================
; Disk tools: a plugin of the WiC64 browser
;
; Uploads files and whole disks from drive 8-11 to the server, which stores them
; in content/prg/From C64 (files as they are, disks as .d64).
;
;   A-T        upload the file; open a folder or disk image (SD2IEC)
;   SHIFT+A-T  upload a disk image file itself (SD2IEC)
;   INST/DEL   one folder up, or out of a disk image (SD2IEC)
;   F3         back up the whole disk as a .d64 (asks for its name; RUN/STOP stops it)
;   F1         read the directory again (e.g. after changing the disk)
;   F5         next drive: 8, 9, 10, 11
;   + / -      next / previous page
;   <-         back to the browser
;
; The browser starts plugins like any program. The server fills in its own
; address after the marker "WIC64-SERVER-ADDRESS" (see server/Programs/Plugins.cs),
; and draws the directory screen like the browser's menus (Uploads/DriveSession.cs).
; Back to the browser, the plugin leaves the server address at $9f00 (see
; "handoff" below), so the browser skips its address screen.
;
; Memory: this program from $0801, data buffer $2000-$9fff (32 KB).
;==============================================================================

!cpu 6502

wic64_optimize_for_size = 1
wic64_include_load_and_run = 1          ; back to the browser
!src "wic64.h"

SETLFS          = $ffba
SETNAM          = $ffbd
OPEN            = $ffc0
CLOSE           = $ffc3
CHKIN           = $ffc6
CHKOUT          = $ffc9
CLRCHN          = $ffcc
CHRIN           = $ffcf
CHROUT          = $ffd2
LOAD            = $ffd5
STOP            = $ffe1
GETIN           = $ffe4
CLALL           = $ffe7
RESTOR          = $ff8a
STATUS          = $90

KEY_F1          = $85
KEY_F3          = $86
KEY_F5          = $87
KEY_DEL         = $14
KEY_SHIFT_A     = $c1
KEY_LEFT_ARROW  = $5f
KEY_A           = $41
KEY_RETURN      = $0d
KEY_RUN_STOP    = $03

ADDRESS_MAX     = 30                    ; the server address, as in the browser
NAME_COLUMN     = 12                    ; the backup name in the status line

screen          = $0400
status_row      = screen + 24*40
color_ram       = $d800
buffer          = $2000
buffer_end      = $a000                 ; 32 KB
ptr             = $fb                   ; zeropage pointers (this program owns the machine)
entry_ptr       = $fd

handoff         = $9f00                 ; for the browser: "SRV!", the address (30 bytes), its length
FILE_CHANNEL    = 2
COMMAND_CHANNEL = 15
TIMEOUT         = $0f

;------------------------------------------------------------------------------
; BASIC stub: 2025 SYS2061
;------------------------------------------------------------------------------

* = $0801
    !word basic_end, 2025
    !byte $9e                           ; SYS
    !text "2061"
    !byte 0
basic_end:
    !word 0

!macro status .message {
    lda #<.message
    ldy #>.message
    jsr show_status
}

start:
    jsr RESTOR                          ; without the browser's LOAD helper: LOAD"$",8 goes to the drive
    lda #0
    sta $9d                             ; no KERNAL messages
    sta $d020
    sta $d021
    lda #$17                            ; upper/lowercase character set
    sta $d018
    lda #$80                            ; no shift+C= charset switching
    sta $0291

    lda address_length
    bne +
    jsr clear_screen
    +status msg_not_from_browser
    rts

+   +wic64_set_timeout TIMEOUT
    tsx
    stx main_stack
    +wic64_set_timeout_handler on_timeout
    +wic64_set_error_handler on_wic64_error
    jmp read_directory

;------------------------------------------------------------------------------
; Main loop
;------------------------------------------------------------------------------

main_loop:
    jsr GETIN
    beq main_loop
    cmp #KEY_F1
    bne +
    jmp read_directory
+   cmp #KEY_F3
    bne +
    jmp backup_disk
+   cmp #KEY_LEFT_ARROW
    bne +
    jmp back_to_browser
+   cmp #'+'
    bne +
    lda page
    clc
    adc #1
    cmp dir_pages
    bcc ++
    lda #0
++  jmp show_page
+   cmp #'-'
    bne +
    lda page
    bne ++
    lda dir_pages
++  sec
    sbc #1
    jmp show_page
+   cmp #KEY_DEL
    bne +
    jmp folder_up
+   cmp #KEY_F5
    bne +
    jmp next_drive
+   ldx #0                              ; A-T: open or upload
    cmp #KEY_A
    bcc +
    cmp #KEY_A+20
    bcs +
    sbc #KEY_A-1                        ; carry is clear: subtract one less
    jmp select_entry
+   ldx #1                              ; SHIFT+A-T: upload, also a disk image file
    cmp #KEY_SHIFT_A
    bcc +
    cmp #KEY_SHIFT_A+20
    bcs +
    sbc #KEY_SHIFT_A-1
    jmp select_entry
+   jmp main_loop

next_drive:
    ldx device
    inx
    cpx #12
    bcc +
    ldx #8
+   stx device
    jmp read_directory

;------------------------------------------------------------------------------
; Directory: LOAD"$",8, sent to the server, which draws the screen
;------------------------------------------------------------------------------

read_directory:
    jsr clear_screen
    jsr draw_title
    +status msg_reading_directory
    lda #1
    ldx #<dollar
    ldy #>dollar
    jsr SETNAM
    lda #1
    ldx device
    ldy #0
    jsr SETLFS
    lda #0
    ldx #<buffer
    ldy #>buffer
    jsr LOAD
    bcc +
    +status msg_no_drive
    jmp main_loop

+   stx data_end
    sty data_end+1
    lda #0
    sta page
    ldx #<path_directory
    ldy #>path_directory
    jsr url_begin
    lda device
    jsr url_add_hex
    jsr post_buffer
    jsr receive_page
    jmp main_loop

show_page:
    sta page
    ldx #<path_page
    ldy #>path_page
    jsr url_begin
    lda page
    jsr url_add_hex
    lda #WIC64_HTTP_GET
    jsr send_request
    jsr receive_page
    jmp main_loop

; count, pages, 20 x (name length, type, name) and the screen
receive_page:
    +wic64_receive dir_count, dir_names_end - dir_count
    +wic64_receive screen, 1000
    +wic64_finalize
    jmp apply_colors

;------------------------------------------------------------------------------
; Upload a file: read it with OPEN "name,P,R" and send it in parts of 32 KB
;------------------------------------------------------------------------------

; A = entry, X = 1 with SHIFT. Folders and disk images are opened (CD), files uploaded.
select_entry:
    cmp dir_count
    bcc +
    jmp main_loop
+   stx shifted
    sta entry
    lda #<dir_names                     ; entry_ptr = dir_names + entry * 18
    sta entry_ptr
    lda #>dir_names
    sta entry_ptr+1
    ldx entry
    beq ++
-   clc
    lda entry_ptr
    adc #18
    sta entry_ptr
    bcc +
    inc entry_ptr+1
+   dex
    bne -

++  ldy #1                              ; type: 0 PRG, 1 SEQ, 2 USR, 3 REL, 4 DEL, 5 folder, 6 disk image
    lda (entry_ptr),y
    cmp #5
    bcc upload_entry
    bne +
    jmp change_directory                ; a folder
+   ldx shifted                         ; a disk image: SHIFT uploads the image file, otherwise open it
    bne +
    jmp change_directory
+   lda #0                              ; the image file is a PRG file on the SD card

upload_entry:
    cmp #3                              ; PRG, SEQ and USR can be read
    bcc +
    +status msg_cannot_read
    jmp main_loop

+   tax
    lda type_letters,x
    sta open_type

    ; open_name = name + ",P,R"
    ldy #0
    lda (entry_ptr),y
    sta name_length
    lda #0
    sta copy_index
-   lda copy_index
    cmp name_length
    beq +
    clc
    adc #2
    tay
    lda (entry_ptr),y
    ldx copy_index
    sta open_name,x
    inc copy_index
    jmp -
+   ldx name_length
    lda #','
    sta open_name,x
    lda open_type
    sta open_name+1,x
    lda #','
    sta open_name+2,x
    lda #'R'
    sta open_name+3,x

    +status msg_uploading
    lda name_length
    clc
    adc #4
    ldx #<open_name
    ldy #>open_name
    jsr SETNAM
    lda #FILE_CHANNEL
    ldx device
    ldy #FILE_CHANNEL
    jsr SETLFS
    jsr OPEN
    bcc +
    +status msg_no_drive
    jmp main_loop
+   ldx #FILE_CHANNEL
    jsr CHKIN

    lda #0
    sta part
next_part:
    lda #<buffer
    sta ptr
    lda #>buffer
    sta ptr+1
read_byte:
    jsr CHRIN
    ldy #0
    sta (ptr),y
    inc ptr
    bne +
    inc ptr+1
    jsr STOP                            ; RUN/STOP every 256 bytes
    beq upload_stopped
+   lda STATUS
    bne end_of_file
    lda ptr+1
    cmp #>buffer_end
    bne read_byte
    lda #0                              ; buffer full: send this part, then read on
    jsr send_part
    inc part
    jmp next_part

end_of_file:
    and #%10111111                      ; $40 = end of file (the last byte is fine), anything else is an error
    bne read_error
    lda #1
    jsr send_part                       ; the server answers with the file name it used
close_file:
    jsr CLRCHN
    lda #FILE_CHANNEL
    jsr CLOSE
    jmp main_loop

read_error:
    jsr CLRCHN
    lda #FILE_CHANNEL
    jsr CLOSE
    jsr show_drive_status               ; e.g. "62,file not found,00,00"
    jmp main_loop

upload_stopped:
    +status msg_stopped
    jmp close_file

; Sends buffer..ptr as part "part" of the file; A = 1 for the last part
send_part:
    sta last_part
    lda ptr
    sta data_end
    lda ptr+1
    sta data_end+1
    ldx #<path_file
    ldy #>path_file
    jsr url_begin
    lda page
    jsr url_add_hex
    lda #'/'
    jsr url_add
    lda entry
    jsr url_add_hex
    lda #'/'
    jsr url_add
    lda part
    jsr url_add_hex
    lda #'/'
    jsr url_add
    lda last_part
    ora #'0'
    jsr url_add
    jsr post_buffer
    lda last_part
    beq +
    +wic64_receive status_row, 40
+   +wic64_finalize
    jmp white_status

;------------------------------------------------------------------------------
; SD2IEC folders and disk images: "CD:name" opens them, "CD:<-" goes back up
;------------------------------------------------------------------------------

change_directory:
    lda #'C'
    sta open_name
    lda #'D'
    sta open_name+1
    lda #':'
    sta open_name+2
    ldy #0
    lda (entry_ptr),y
    sta name_length
    lda #0
    sta copy_index
-   lda copy_index
    cmp name_length
    beq +
    clc
    adc #2
    tay
    lda (entry_ptr),y
    ldx copy_index
    sta open_name+3,x
    inc copy_index
    bne -
+   lda name_length
    clc
    adc #3
    jmp directory_command

folder_up:
    lda #KEY_LEFT_ARROW
    sta open_name+3
    lda #'C'
    sta open_name
    lda #'D'
    sta open_name+1
    lda #':'
    sta open_name+2
    lda #4

; Sends the command in open_name (A = length); when the drive accepts it, reads the new directory
directory_command:
    ldx #<open_name
    ldy #>open_name
    jsr drive_command
    bcs +
    jmp read_directory
+   jmp main_loop                       ; the drive's message stays in the status line

;------------------------------------------------------------------------------
; Back up the whole disk: every sector with "U1", one POST per track
;------------------------------------------------------------------------------

backup_disk:
    lda #0
    jsr SETNAM
    lda #COMMAND_CHANNEL
    ldx device
    ldy #COMMAND_CHANNEL
    jsr SETLFS
    jsr OPEN
    bcc +
    +status msg_no_drive
    jmp main_loop
+   lda #1
    ldx #<hash
    ldy #>hash
    jsr SETNAM
    lda #FILE_CHANNEL
    ldx device
    ldy #FILE_CHANNEL
    jsr SETLFS
    jsr OPEN

    ; first a test read of track 18 sector 0 (the disk's name and BAM): an SD2IEC folder is no disk,
    ; nor is an empty 1541
    lda #18
    sta track
    jsr track_start
    jsr read_sector
    cmp #2
    bcc +
    jsr close_channels
    +status msg_not_a_disk
    jmp main_loop

+   jsr ask_backup_name
    bcc +
    jsr close_channels
    +status msg_cancelled
    jmp main_loop
+   +status msg_backup

    lda #1
    sta track
track_loop:
    jsr track_start
sector_loop:
    jsr read_sector
    ldx sector
    sta sector_status,x

    jsr STOP
    bne +
    jmp backup_stopped
+   inc sector
    ldx track
    lda sector
    cmp sectors_per_track-1,x
    bcs +
    jmp sector_loop
+
    ; the track: its sectors, then one status code per sector
    ldy #0
-   lda sector_status,y
    sta (ptr),y
    iny
    cpy sector
    bne -
    tya
    clc
    adc ptr
    sta data_end
    lda ptr+1
    adc #0
    sta data_end+1
    ldx #<path_track
    ldy #>path_track
    jsr url_begin
    lda track
    jsr url_add_hex
    lda track                           ; track 1 also carries the name: "/" and PETSCII in hex
    cmp #1
    bne ++
    ldy disk_name_length
    beq ++
    lda #'/'
    jsr url_add
    ldy #0
-   lda disk_name,y
    jsr url_add_hex
    iny
    cpy disk_name_length
    bne -
++  jsr post_buffer
    +wic64_receive status_row, 40       ; "Backing up: track 12 of 35 done", at the end the file name
    +wic64_finalize
    jsr white_status

    inc track
    lda track
    cmp #36
    bcs close_backup
    jmp track_loop

close_backup:
    jsr close_channels
    jmp main_loop

close_channels:
    jsr CLRCHN
    lda #FILE_CHANNEL
    jsr CLOSE
    lda #COMMAND_CHANNEL
    jmp CLOSE

backup_stopped:
    +status msg_stopped
    jmp close_backup

; "U1 2 0 track sector" reads the sector into the drive buffer of channel 2, then its 256 bytes go to ptr
; (ptr moves on a page). Returns the drive's status code in A: 0 = fine, e.g. 21 = read error.
read_sector:
    ldx #COMMAND_CHANNEL
    jsr CHKOUT
    lda #0
    sta copy_index
-   ldx copy_index
    lda u1_command,x
    beq +
    jsr CHROUT
    inc copy_index
    bne -
+   lda track
    jsr print_decimal
    lda #' '
    jsr CHROUT
    lda sector
    jsr print_decimal
    lda #KEY_RETURN
    jsr CHROUT
    jsr CLRCHN

    ; the drive's answer, "00, ok,00,00" or e.g. "21,read error,..": keep the number
    ldx #COMMAND_CHANNEL
    jsr CHKIN
    jsr CHRIN
    and #$0f
    asl
    sta code
    asl
    asl
    clc
    adc code                            ; tens * 10
    sta code
    jsr CHRIN
    and #$0f
    clc
    adc code
    sta code
-   lda STATUS                          ; skip the rest of the line
    bne +
    jsr CHRIN
    cmp #KEY_RETURN
    bne -
+   jsr CLRCHN

    ; its 256 bytes
    ldx #FILE_CHANNEL
    jsr CHKIN
    lda #0
    sta copy_index
-   jsr CHRIN
    ldy copy_index
    sta (ptr),y
    inc copy_index
    bne -
    jsr CLRCHN
    inc ptr+1
    lda code
    rts

track_start:
    lda #<buffer
    sta ptr
    lda #>buffer
    sta ptr+1
    lda #0
    sta sector
    rts

; The name of the .d64 in the status line, proposed: the disk's name. RETURN accepts it (carry clear),
; <- or RUN/STOP cancels (carry set). Row 23 shows the keys meanwhile.
ask_backup_name:
    ldx #39
-   lda screen + 23*40,x
    sta saved_row,x
    lda msg_name_keys,x
    sta screen + 23*40,x
    dex
    bpl -
    +status msg_backup_name

name_loop:
    ldx #0
-   cpx disk_name_length
    bcs +
    lda disk_name,x
    jsr petscii_to_screen_code
    sta status_row + NAME_COLUMN,x
    inx
    bne -
+   lda #$a0                            ; cursor: reverse space
    sta status_row + NAME_COLUMN,x
    lda #' '
-   inx
    cpx #17
    bcs +
    sta status_row + NAME_COLUMN,x
    bne -

+
name_key:
    jsr GETIN
    beq name_key
    cmp #KEY_RETURN
    beq name_done                       ; carry is set
    cmp #KEY_LEFT_ARROW
    beq name_cancelled
    cmp #KEY_RUN_STOP
    beq name_cancelled
    ldx disk_name_length
    cmp #KEY_DEL
    bne +
    txa
    beq name_loop
    dec disk_name_length
    jmp name_loop
+   cpx #16
    bcs name_loop
    cmp #'"'
    beq name_loop
    cmp #$20                            ; $20-$3f, letters $41-$5a and shifted letters $c1-$da
    bcc name_loop
    cmp #$40
    bcc +
    beq name_loop
    cmp #$5b
    bcc +
    cmp #KEY_SHIFT_A
    bcc name_loop
    cmp #KEY_SHIFT_A+26
    bcs name_loop
+   sta disk_name,x
    inc disk_name_length
    jmp name_loop

name_cancelled:
    sec
    !byte $24                           ; bit zp: skips the clc
name_done:
    clc
    php
    ldx #39
-   lda saved_row,x
    sta screen + 23*40,x
    dex
    bpl -
    plp
    rts

; Prints A (0-99) as a decimal number to the current output channel
print_decimal:
    ldx #0
-   cmp #10
    bcc +
    sbc #10                             ; carry is set
    inx
    bne -
+   pha
    txa
    beq +
    ora #'0'
    jsr CHROUT
+   pla
    ora #'0'
    jmp CHROUT

;------------------------------------------------------------------------------
; Back to the browser: load it from the server and run it
;------------------------------------------------------------------------------

back_to_browser:
    +status msg_loading_browser
    ldx #ADDRESS_MAX-1                  ; the address for the browser, in its own layout
-   lda server_address,x
    sta handoff+4,x
    dex
    bpl -
    lda address_length
    sta handoff+4+ADDRESS_MAX
    ldx #3
-   lda handoff_magic,x
    sta handoff,x
    dex
    bpl -
    ldx #<path_browser
    ldy #>path_browser
    jsr url_begin
    lda #WIC64_HTTP_GET
    sta request_command
    jsr set_request_length
    +wic64_load_and_run request, TIMEOUT
    jmp on_timeout

;------------------------------------------------------------------------------
; HTTP
;------------------------------------------------------------------------------

; POSTs buffer..data_end to the URL, then receives the response header and the status byte
post_buffer:
    lda #WIC64_HTTP_POST_URL
    sta request_command
    jsr set_request_length
    +wic64_execute request
    sec
    lda data_end
    sbc #<buffer
    sta post_length
    lda data_end+1
    sbc #>buffer
    sta post_length+1
    +wic64_initialize
    +wic64_send_header post_request
    +wic64_send buffer, ~post_length
    jmp receive_status

; A = WiC64 command: sends the request with the URL, receives the response header and the status byte
send_request:
    sta request_command
    jsr set_request_length
    +wic64_initialize
    +wic64_send_header request
    +wic64_send

receive_status:
    +wic64_receive_header
    +wic64_receive app_status, 1
    lda app_status
    bne +
    rts
+   +wic64_receive status_row, 40       ; the server's error message
    +wic64_finalize
    jmp show_error

set_request_length:
    lda url_length
    sta request_length
    lda #0
    sta request_length+1
    rts

; url = "http://" + server address + "/" + the path at X/Y (0-terminated)
url_begin:
    stx path_source+1
    sty path_source+2
    ldx #0
-   lda url_scheme,x
    beq +
    sta request_url,x
    inx
    bne -
+   stx url_length
    ldy #0
-   lda server_address,y
    jsr url_add
    iny
    cpy address_length
    bne -
    lda #'/'
    jsr url_add
    ldy #0
path_source:
-   lda $ffff,y
    beq +
    jsr url_add
    iny
    bne -
+   rts

url_add_hex:
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
    adc #'a'-'0'-10-1                   ; carry is set
+   adc #'0'

url_add:
    ldx url_length
    sta request_url,x
    inc url_length
    rts

;------------------------------------------------------------------------------
; Errors
;------------------------------------------------------------------------------

on_timeout:
    +status msg_timeout
    jmp show_error

on_wic64_error:
    +wic64_execute status_request, status_row
    ldx #0
-   lda status_row,x
    beq +
    jsr petscii_to_screen_code
    sta status_row,x
    inx
    cpx #40
    bne -
+   lda #' '
-   cpx #40
    beq show_error
    sta status_row,x
    inx
    bne -

; The status line in light red; back to the main loop
show_error:
    ldx main_stack
    txs
    jsr CLRCHN
    jsr CLALL                           ; files that were open when it went wrong
    ldx #39
    lda #10
-   sta color_ram + 24*40,x
    dex
    bpl -
    jmp main_loop

; Reads the drive's status line (e.g. "62,file not found,00,00") into the status line
show_drive_status:
    lda #0

; Sends the command A/X/Y (as for SETNAM, A = 0: none) to the drive and shows its answer in the status line.
; Carry set when the answer is an error (20 or higher) or there is no drive.
drive_command:
    jsr SETNAM
    lda #COMMAND_CHANNEL
    ldx device
    ldy #COMMAND_CHANNEL
    jsr SETLFS
    jsr OPEN
    ldx #COMMAND_CHANNEL
    jsr CHKIN
    lda #0
    sta copy_index
-   jsr CHRIN
    cmp #KEY_RETURN
    beq +
    jsr petscii_to_screen_code
    ldx copy_index
    sta status_row,x
    inc copy_index
    lda STATUS
    bne +
    cpx #39
    bne -
+   ldx copy_index
    lda #' '
-   cpx #40
    beq +
    sta status_row,x
    inx
    bne -
+   jsr CLRCHN
    lda #COMMAND_CHANNEL
    jsr CLOSE
    jsr white_status
    lda status_row                      ; status 00-19 are no errors
    cmp #'2'
    rts

petscii_to_screen_code:
    cmp #$40
    bcc +++                             ; $20-$3f stay the same
    cmp #$60
    bcs +
    and #$3f                            ; $40-$5f -> $00-$1f
    rts
+   cmp #$80
    bcs +
    sbc #$20-1                          ; $60-$7f -> $40-$5f (carry is clear)
    rts
+   cmp #$c0
    bcc ++
    cmp #$e0
    bcs ++
    and #$7f                            ; $c0-$df -> $40-$5f
    rts
++  lda #' '
+++ rts

;------------------------------------------------------------------------------
; Screen
;------------------------------------------------------------------------------

clear_screen:
    ldx #0
    lda #' '
-   sta screen,x
    sta screen+$100,x
    sta screen+$200,x
    sta screen+$2e8,x
    inx
    bne -
    ; fall through

; The browser's colors: title cyan, entry letters yellow, help grey, status white, the rest light grey
apply_colors:
    ldx #0
    lda #15
-   sta color_ram,x
    sta color_ram+$100,x
    sta color_ram+$200,x
    sta color_ram+$2e8,x
    inx
    bne -
    ldx #39
-   lda #3
    sta color_ram,x
    lda #12
    sta color_ram + 22*40,x
    sta color_ram + 23*40,x
    dex
    bpl -
    lda #7
!for row, 2, 21 {
    sta color_ram + row*40 + 1
}
    ; fall through

white_status:
    ldx #39
    lda #1
-   sta color_ram + 24*40,x
    dex
    bpl -
    rts

; Row 0 before the server draws the screen: " Drive 8", so it stays when the drive doesn't answer
draw_title:
    ldx #39
    lda #$a0                            ; reverse space
-   sta screen,x
    dex
    bpl -
    ldx #0
-   lda msg_drive,x
    ora #$80
    sta screen,x
    inx
    cpx #7
    bne -
    lda device                          ; 8-11
    cmp #10
    bcc +
    pha
    lda #'1' | $80
    sta screen,x
    inx
    pla
    sbc #10                             ; carry is still set
+   ora #'0' | $80
    sta screen,x
    rts

; Shows the screen code string at A/Y (terminated by $ff) in the status line, in white
show_status:
    sta status_source+1
    sty status_source+2
    ldx #0
status_source:
-   lda $ffff,x
    cmp #$ff
    beq +
    sta status_row,x
    inx
    cpx #40
    bne -
    jmp white_status
+   lda #' '
-   sta status_row,x
    inx
    cpx #40
    bne -
    jmp white_status

;------------------------------------------------------------------------------
; Data
;------------------------------------------------------------------------------

; The server writes its address here when it sends this plugin to the C64
server_marker:  !text "WIC64-SERVER-ADDRESS"
address_length: !byte 0
server_address: !fill ADDRESS_MAX, 0
handoff_magic:  !text "SRV!"             ; the same in c64/browser.asm

url_scheme:     !text "http://", 0
path_directory: !text "d/dir/", 0
path_page:      !text "d/page/", 0
path_file:      !text "d/file/", 0
path_track:     !text "d/track/", 0
path_browser:   !text "browser.prg", 0
dollar:         !text "$"
hash:           !text "#"
u1_command:     !text "U1 2 0 ", 0
type_letters:   !text "PSU"

sectors_per_track:
                !fill 17, 21            ; tracks 1-17
                !fill 7, 19             ; 18-24
                !fill 6, 18             ; 25-30
                !fill 5, 17             ; 31-35

msg_drive:              !scr " Drive "
msg_reading_directory:  !scr "Reading the directory...", $ff
msg_backup_name:        !scr "Back up as:", $ff
msg_name_keys:          !scr " RETURN: back up the disk  ", $1f, ": cancel    "
msg_cancelled:          !scr "Cancelled", $ff
msg_not_a_disk:         !scr "No disk to back up (open a .d64 first)", $ff
msg_no_drive:           !scr "No drive here (or no disk) - F5: next", $ff
msg_uploading:          !scr "Uploading... (RUN/STOP stops)", $ff
msg_cannot_read:        !scr "This entry can't be uploaded", $ff
msg_stopped:            !scr "Stopped", $ff
msg_backup:             !scr "Backing up the disk... (RUN/STOP stops)", $ff
msg_loading_browser:    !scr "Loading the browser...", $ff
msg_timeout:            !scr "Timeout: is the server on?", $ff
msg_not_from_browser:   !scr "Start this plugin from the WiC64 browser", $ff

status_request: !byte "R", WIC64_GET_STATUS_MESSAGE, $01, $00, $00

post_request:   !byte "R", WIC64_HTTP_POST_DATA
post_length:    !word 0

request:        !byte "R"
request_command: !byte 0
request_length: !word 0
request_url:    !fill 112, 0             ; up to the address, "/d/track/01/" and a name of 16 in hex
url_length:     !byte 0

main_stack:     !byte 0
device:         !byte 8
shifted:        !byte 0
page:           !byte 0
entry:          !byte 0
part:           !byte 0
last_part:      !byte 0
track:          !byte 0
sector:         !byte 0
code:           !byte 0
copy_index:     !byte 0
name_length:    !byte 0
open_type:      !byte 0
app_status:     !byte 0
data_end:       !word 0
open_name:      !fill 20, 0
sector_status:  !fill 21, 0
saved_row:      !fill 40, 0

dir_count:      !byte 0                 ; received together: count, pages, names, disk name
dir_pages:      !byte 1
dir_names:      !fill 20 * 18, 0        ; name length, type, name (16)
disk_name_length: !byte 0
disk_name:      !fill 16, 0             ; PETSCII; edited as the name of a backup
dir_names_end:

!src "wic64.asm"

!if * > buffer {
    !error "the plugin does not fit below its buffer at $2000"
}
