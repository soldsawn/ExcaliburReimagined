// SPDX-License-Identifier: GPL-2.0 OR MIT
/*
 * excalibur_ec: Excalibur Reimagined's small helper driver for Casper Excalibur laptops.
 *
 * Binds to the BIOS's GWMI data block (GUID 644C5791-B7B0-4123-A90B-E93876E0DAAD, object "AA") and offers
 * /dev/excalibur_ec (root only) with three ioctls:
 *   - EXR_WMI_CALL: the same 32-byte block the Windows app sends through RW_GMWMI (set = WSAA, then query = WQAA),
 *     so the BIOS itself applies a power mode (EC + NVIDIA Dynamic Boost + DPTF) or the keyboard light, and GET
 *     0x0200 returns temperatures and fan speeds. Only the reads (0xFA00) and the sets the app uses are let through;
 *     the others (e.g. 0xFB00/0x0203, the display switch, which writes CMOS) are refused.
 *   - EXR_EC_READ: any EC register, through the kernel's EC driver (queued with ACPI's own accesses).
 *   - EXR_EC_WRITE: only the two fan target registers.
 * Works with Secure Boot when the module is signed (DKMS signs it with the machine's MOK), where ec_sys and /dev/mem
 * are closed by the kernel's lockdown.
 */

#include <linux/acpi.h>
#include <linux/fs.h>
#include <linux/miscdevice.h>
#include <linux/module.h>
#include <linux/mutex.h>
#include <linux/slab.h>
#include <linux/uaccess.h>
#include <linux/wmi.h>

#include "excalibur_ec.h"

#define GWMI_GUID "644C5791-B7B0-4123-A90B-E93876E0DAAD"

#define OP_GET 0xFA00
#define OP_SET 0xFB00

static DEFINE_MUTEX(exr_lock);
static struct wmi_device *exr_wdev; /* guarded by exr_lock */

static bool exr_wmi_allowed(const u8 *buf)
{
	u16 op = buf[0] | buf[1] << 8;
	u16 fn = buf[2] | buf[3] << 8;

	if (op == OP_GET)
		return true; /* every GET case of WSAA only reads */
	if (op == OP_SET)
		return fn == 0x0100  /* keyboard light */
		    || fn == 0x0205  /* fan targets (empty on the G870; used where a BIOS has it) */
		    || fn == 0x0300; /* performance mode */
	return false;
}

static bool exr_ec_write_allowed(u8 addr)
{
	return addr == 0x2C || addr == 0x2D; /* CPU / GPU fan target */
}

static long exr_wmi_call(struct exr_wmi_call __user *arg)
{
	struct exr_wmi_call call;
	struct acpi_buffer in = { sizeof(call.buf), call.buf };
	union acpi_object *obj;
	acpi_status status;
	long ret = 0;

	if (copy_from_user(&call, arg, sizeof(call)))
		return -EFAULT;
	if (!exr_wmi_allowed(call.buf))
		return -EPERM;

	mutex_lock(&exr_lock);
	if (!exr_wdev) {
		ret = -ENODEV;
		goto out;
	}
	status = wmidev_block_set(exr_wdev, 0, &in);
	if (ACPI_FAILURE(status)) {
		ret = -EIO;
		goto out;
	}
	obj = wmidev_block_query(exr_wdev, 0);
	if (!obj) {
		ret = -EIO;
		goto out;
	}
	if (obj->type != ACPI_TYPE_BUFFER || obj->buffer.length < sizeof(call.buf)) {
		ret = -EPROTO;
	} else {
		memcpy(call.buf, obj->buffer.pointer, sizeof(call.buf));
	}
	kfree(obj);
out:
	mutex_unlock(&exr_lock);
	if (!ret && copy_to_user(arg, &call, sizeof(call)))
		ret = -EFAULT;
	return ret;
}

static long exr_ioctl(struct file *file, unsigned int cmd, unsigned long arg)
{
	struct exr_ec_reg reg;
	void __user *uarg = (void __user *)arg;
	int ret;

	switch (cmd) {
	case EXR_VERSION: {
		__u32 v = EXR_ABI_VERSION;

		return copy_to_user(uarg, &v, sizeof(v)) ? -EFAULT : 0;
	}
	case EXR_EC_READ:
		if (copy_from_user(&reg, uarg, sizeof(reg)))
			return -EFAULT;
		ret = ec_read(reg.addr, &reg.value);
		if (ret)
			return ret;
		return copy_to_user(uarg, &reg, sizeof(reg)) ? -EFAULT : 0;
	case EXR_EC_WRITE:
		if (copy_from_user(&reg, uarg, sizeof(reg)))
			return -EFAULT;
		if (!exr_ec_write_allowed(reg.addr))
			return -EPERM;
		return ec_write(reg.addr, reg.value);
	case EXR_WMI_CALL:
		return exr_wmi_call(uarg);
	default:
		return -ENOTTY;
	}
}

static const struct file_operations exr_fops = {
	.owner = THIS_MODULE,
	.unlocked_ioctl = exr_ioctl,
	.compat_ioctl = compat_ptr_ioctl,
};

static struct miscdevice exr_misc = {
	.minor = MISC_DYNAMIC_MINOR,
	.name = "excalibur_ec",
	.fops = &exr_fops,
	.mode = 0600,
};

static int exr_probe(struct wmi_device *wdev, const void *context)
{
	int ret;

	if (!wdev->setable)
		return -ENODEV;
	mutex_lock(&exr_lock);
	if (exr_wdev) {
		mutex_unlock(&exr_lock);
		return -EBUSY;
	}
	exr_wdev = wdev;
	mutex_unlock(&exr_lock);

	ret = misc_register(&exr_misc);
	if (ret) {
		mutex_lock(&exr_lock);
		exr_wdev = NULL;
		mutex_unlock(&exr_lock);
	}
	return ret;
}

static void exr_remove(struct wmi_device *wdev)
{
	misc_deregister(&exr_misc);
	mutex_lock(&exr_lock);
	exr_wdev = NULL;
	mutex_unlock(&exr_lock);
}

static const struct wmi_device_id exr_id_table[] = {
	{ .guid_string = GWMI_GUID },
	{ }
};
MODULE_DEVICE_TABLE(wmi, exr_id_table);

static struct wmi_driver exr_driver = {
	.driver = {
		.name = "excalibur_ec",
	},
	.id_table = exr_id_table,
	.probe = exr_probe,
	.remove = exr_remove,
	.no_singleton = false,
};
module_wmi_driver(exr_driver);

MODULE_AUTHOR("soldsawn");
MODULE_DESCRIPTION("Excalibur Reimagined: Casper Excalibur BIOS (GWMI) and EC access for the fan service");
MODULE_LICENSE("Dual MIT/GPL");
