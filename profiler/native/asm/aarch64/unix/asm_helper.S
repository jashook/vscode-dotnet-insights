.section __TEXT,__text

.globl _EnterWrapper
.globl _ExitWrapper
.globl _TailcallWrapper

_EnterWrapper:

    str   x0, [sp, #-8]!         
    str   x1, [sp, #-8]!         
    str   x2, [sp, #-8]!         
    str   x3, [sp, #-8]!         
    str   x4, [sp, #-8]!         
    str   x5, [sp, #-8]!         
    str   x6, [sp, #-8]!         
    str   x7, [sp, #-8]!         
    str   x8, [sp, #-8]!         
    str   x9, [sp, #-8]!         
    str   x10, [sp, #-8]!         
    str   x11, [sp, #-8]!         
    str   x12, [sp, #-8]!         
    str   x13, [sp, #-8]!         
    str   x14, [sp, #-8]!         
    str   x15, [sp, #-8]!         
    str   x16, [sp, #-8]!         
    str   x17, [sp, #-8]!         
    str   x18, [sp, #-8]!         
    str   x19, [sp, #-8]!         
    str   x20, [sp, #-8]!         
    str   x21, [sp, #-8]!         
    str   x22, [sp, #-8]!         
    str   x23, [sp, #-8]!         
    str   x24, [sp, #-8]!         
    str   x25, [sp, #-8]!         
    str   x26, [sp, #-8]!         
    str   x27, [sp, #-8]!         
    str   x28, [sp, #-8]!         
    str   x29, [sp, #-8]!         
    str   x30, [sp, #-8]!         

    b _EnterStub
    ldr   x0, [sp], #8           
    ldr   x1, [sp], #8           
    ldr   x2, [sp], #8           
    ldr   x3, [sp], #8           
    ldr   x4, [sp], #8           
    ldr   x5, [sp], #8           
    ldr   x6, [sp], #8           
    ldr   x7, [sp], #8           
    ldr   x8, [sp], #8           
    ldr   x9, [sp], #8           
    ldr   x10, [sp], #8           
    ldr   x11, [sp], #8           
    ldr   x12, [sp], #8           
    ldr   x13, [sp], #8           
    ldr   x14, [sp], #8           
    ldr   x15, [sp], #8           
    ldr   x16, [sp], #8           
    ldr   x17, [sp], #8           
    ldr   x18, [sp], #8           
    ldr   x19, [sp], #8           
    ldr   x20, [sp], #8           
    ldr   x21, [sp], #8           
    ldr   x22, [sp], #8           
    ldr   x23, [sp], #8           
    ldr   x24, [sp], #8           
    ldr   x25, [sp], #8           
    ldr   x26, [sp], #8           
    ldr   x27, [sp], #8           
    ldr   x28, [sp], #8           
    ldr   x29, [sp], #8           
    ldr   x30, [sp], #8           
    ret

_ExitWrapper:
    str   x0, [sp, #-8]!         
    str   x1, [sp, #-8]!         
    str   x2, [sp, #-8]!         
    str   x3, [sp, #-8]!         
    str   x4, [sp, #-8]!         
    str   x5, [sp, #-8]!         
    str   x6, [sp, #-8]!         
    str   x7, [sp, #-8]!         
    str   x8, [sp, #-8]!         
    str   x9, [sp, #-8]!         
    str   x10, [sp, #-8]!         
    str   x11, [sp, #-8]!         
    str   x12, [sp, #-8]!         
    str   x13, [sp, #-8]!         
    str   x14, [sp, #-8]!         
    str   x15, [sp, #-8]!         
    str   x16, [sp, #-8]!         
    str   x17, [sp, #-8]!         
    str   x18, [sp, #-8]!         
    str   x19, [sp, #-8]!         
    str   x20, [sp, #-8]!         
    str   x21, [sp, #-8]!         
    str   x22, [sp, #-8]!         
    str   x23, [sp, #-8]!         
    str   x24, [sp, #-8]!         
    str   x25, [sp, #-8]!         
    str   x26, [sp, #-8]!         
    str   x27, [sp, #-8]!         
    str   x28, [sp, #-8]!         
    str   x29, [sp, #-8]!         
    str   x30, [sp, #-8]!
    b _ExitStub
    ldr   x0, [sp], #8           
    ldr   x1, [sp], #8           
    ldr   x2, [sp], #8           
    ldr   x3, [sp], #8           
    ldr   x4, [sp], #8           
    ldr   x5, [sp], #8           
    ldr   x6, [sp], #8           
    ldr   x7, [sp], #8           
    ldr   x8, [sp], #8           
    ldr   x9, [sp], #8           
    ldr   x10, [sp], #8           
    ldr   x11, [sp], #8           
    ldr   x12, [sp], #8           
    ldr   x13, [sp], #8           
    ldr   x14, [sp], #8           
    ldr   x15, [sp], #8           
    ldr   x16, [sp], #8           
    ldr   x17, [sp], #8           
    ldr   x18, [sp], #8           
    ldr   x19, [sp], #8           
    ldr   x20, [sp], #8           
    ldr   x21, [sp], #8           
    ldr   x22, [sp], #8           
    ldr   x23, [sp], #8           
    ldr   x24, [sp], #8           
    ldr   x25, [sp], #8           
    ldr   x26, [sp], #8           
    ldr   x27, [sp], #8           
    ldr   x28, [sp], #8           
    ldr   x29, [sp], #8           
    ldr   x30, [sp], #8
    ret

_TailcallWrapper:

    str   x0, [sp, #-8]!         
    str   x1, [sp, #-8]!         
    str   x2, [sp, #-8]!         
    str   x3, [sp, #-8]!         
    str   x4, [sp, #-8]!         
    str   x5, [sp, #-8]!         
    str   x6, [sp, #-8]!         
    str   x7, [sp, #-8]!         
    str   x8, [sp, #-8]!         
    str   x9, [sp, #-8]!         
    str   x10, [sp, #-8]!         
    str   x11, [sp, #-8]!         
    str   x12, [sp, #-8]!         
    str   x13, [sp, #-8]!         
    str   x14, [sp, #-8]!         
    str   x15, [sp, #-8]!         
    str   x16, [sp, #-8]!         
    str   x17, [sp, #-8]!         
    str   x18, [sp, #-8]!         
    str   x19, [sp, #-8]!         
    str   x20, [sp, #-8]!         
    str   x21, [sp, #-8]!         
    str   x22, [sp, #-8]!         
    str   x23, [sp, #-8]!         
    str   x24, [sp, #-8]!         
    str   x25, [sp, #-8]!         
    str   x26, [sp, #-8]!         
    str   x27, [sp, #-8]!         
    str   x28, [sp, #-8]!         
    str   x29, [sp, #-8]!         
    str   x30, [sp, #-8]!
    b _TailcallStub
    ldr   x0, [sp], #8           
    ldr   x1, [sp], #8           
    ldr   x2, [sp], #8           
    ldr   x3, [sp], #8           
    ldr   x4, [sp], #8           
    ldr   x5, [sp], #8           
    ldr   x6, [sp], #8           
    ldr   x7, [sp], #8           
    ldr   x8, [sp], #8           
    ldr   x9, [sp], #8           
    ldr   x10, [sp], #8           
    ldr   x11, [sp], #8           
    ldr   x12, [sp], #8           
    ldr   x13, [sp], #8           
    ldr   x14, [sp], #8           
    ldr   x15, [sp], #8           
    ldr   x16, [sp], #8           
    ldr   x17, [sp], #8           
    ldr   x18, [sp], #8           
    ldr   x19, [sp], #8           
    ldr   x20, [sp], #8           
    ldr   x21, [sp], #8           
    ldr   x22, [sp], #8           
    ldr   x23, [sp], #8           
    ldr   x24, [sp], #8           
    ldr   x25, [sp], #8           
    ldr   x26, [sp], #8           
    ldr   x27, [sp], #8           
    ldr   x28, [sp], #8           
    ldr   x29, [sp], #8           
    ldr   x30, [sp], #8
    ret