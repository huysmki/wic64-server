;==============================================================================
; Standalone SID player
;
; Runs from the tape buffer when a tune needs the browser's memory or installs
; its own interrupts. The server fills in the parameters below and sends this
; player right in front of the tune data. The player receives the tune itself
; (it may overwrite the browser), starts it and lets 1-9 select a subtune.
; Reset the C64 to return to the browser.
;==============================================================================

!cpu 6502

GETIN           = $ffe4
KERNAL_IRQ      = $ea31

* = $0334
    jmp start

; Parameters, filled in by the server (see SidService.StandalonePlayer)
count:          !word 0                 ; +3  size of the tune
destination:    !word 0                 ; +5  load address
init:           !word 0                 ; +7
play:           !word 0                 ; +9  0 = the tune installs its own interrupt
bank:           !byte $37               ; +11 value for $01
timer:          !word 0                 ; +12 CIA timer for the play interrupt, 0 = keep the KERNAL's
song:           !byte 0                 ; +14 start song (0-based)
songs:          !byte 0                 ; +15

start:
    ; receive the tune without timeout detection, like wic64_load_and_run
    lda destination
    sta store+1
    lda destination+1
    sta store+2
receive:
-   lda $dd0d
    and #$10
    beq -
    lda $dd01
store:
    sta $ffff
    inc store+1
    bne +
    inc store+2
+   lda count
    bne +
    dec count+1
+   dec count
    lda count
    ora count+1
    bne receive

    lda #$00                            ; userport back to input, like wic64_finalize
    sta $dd03
    lda $dd0d

    lda bank
    sta $01
    lda play
    sta play_call+1
    lda play+1
    sta play_call+2
    ora play
    beq start_tune                      ; the tune installs its own interrupt

    lda #<irq                           ; play from the CIA timer interrupt
    sta $0314
    lda #>irq
    sta $0315
    lda timer
    ora timer+1
    beq start_tune
    lda timer
    sta $dc04
    lda timer+1
    sta $dc05
    lda #$11                            ; load the new value and keep running
    sta $dc0e

start_tune:
    jsr init_song
    cli

    ; 1-9 selects a subtune
loop:
    jsr GETIN
    cmp #'1'
    bcc loop
    cmp #'9'+1
    bcs loop
    sbc #'1'-1                          ; carry is clear: subtract one less
    cmp songs
    bcs loop
    ldx play_call+2                     ; tunes with their own interrupt can not be restarted
    beq loop
    sta song
    sei
    jsr init_song
    cli
    jmp loop

init_song:
    lda #0
    ldx #$18
-   sta $d400,x
    dex
    bpl -
    lda song
    ldx #0
    ldy #0
    jmp (init)

irq:
play_call:
    jsr $ffff
    jmp KERNAL_IRQ                      ; keyboard scan, acknowledges the CIA

!if * > $0400 {
    !error "standalone player does not fit in the tape buffer"
}
