/* SPDX-License-Identifier: GPL-2.0 OR MIT */
/*
 * The ioctls of /dev/excalibur_ec. The fan service calls them from C# (src/Excalibur.Linux/ExcaliburEcDevice.cs keeps
 * the same numbers): EXR_VERSION 0x80047800, EXR_EC_READ 0xC0027801, EXR_EC_WRITE 0x40027802, EXR_WMI_CALL 0xC0207803.
 */
#ifndef EXCALIBUR_EC_H
#define EXCALIBUR_EC_H

#include <linux/ioctl.h>
#include <linux/types.h>

#define EXR_ABI_VERSION 1

struct exr_ec_reg {
	__u8 addr;
	__u8 value;
};

/* The BIOS's 32-byte block: u16 A0 (0xFA00 GET / 0xFB00 SET), u16 A1 (function), u32 A2..A6, 8 bytes unused. */
struct exr_wmi_call {
	__u8 buf[32];
};

#define EXR_VERSION  _IOR('x', 0, __u32)
#define EXR_EC_READ  _IOWR('x', 1, struct exr_ec_reg)
#define EXR_EC_WRITE _IOW('x', 2, struct exr_ec_reg)
#define EXR_WMI_CALL _IOWR('x', 3, struct exr_wmi_call)

#endif
