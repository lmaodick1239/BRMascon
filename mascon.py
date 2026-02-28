'''
# 1 second from N to B8(full brake) (All trains). Note that some trains uses non combined handles, but can be configured to use combined handles.
# 4 notches of power (Class 800), 7 notches (Class 90), 6 notches (Class 230). Find a way for user to configure the number of notches. 

# N to B8 is held by S key. Opposite is W key. Power notches are press by w key 1 time to increment 1 notch, and s key 1 time to decrement 1 notch.

EB is held by E key. Release EB by holding W for 0.2 seconds.

Mascon is zuiki one handle mascon, aka vendor 33dd product id 0001. 


below are values of V1 from P5 to EB, where N is 0, P5 is -32768, and EB is 32767. These values are from Mascon.
The mascon handle is represented by y axis movement from windows, and axis 1.
-32768
-26344
-20176
-14264
-8096
0
6810
10408
14006
17346
20944
24542
27884
31482
32767
'''