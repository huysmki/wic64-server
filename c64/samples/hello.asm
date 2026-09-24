; A tiny BASIC program to test the program loader:
;   10 PRINT "HELLO FROM YOUR MAC!"
;   20 GOTO 10

* = $0801
line10:
    !word line20, 10
    !byte $99                           ; PRINT
    !pet $22, "hello from your mac! ", $22, 0
line20:
    !word end, 20
    !byte $89                           ; GOTO
    !text "10"
    !byte 0
end:
    !word 0
