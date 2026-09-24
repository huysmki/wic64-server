; Tests the LOAD helper: a BASIC program that loads the next program from "drive 8"
;   10 PRINT "LOADING HELLO FROM THE SERVER..."
;   20 LOAD "HELLO",8
; LOAD in a running BASIC program loads the file and starts it (chaining).

* = $0801
line10:
    !word line20, 10
    !byte $99                           ; PRINT
    !pet $22, "loading hello from the server...", $22, 0
line20:
    !word end, 20
    !byte $93                           ; LOAD
    !pet $22, "hello", $22, ",8", 0
end:
    !word 0
