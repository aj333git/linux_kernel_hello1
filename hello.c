#include <linux/module.h>
#include <linux/kernel.h>
#include <linux/init.h>

static int __init hello_init(void)
{
    pr_info("hello: module2 loaded\n");
    return 0;
}

static void __exit hello_exit(void)
{
    pr_info("hello: module2 unloaded\n");
}

module_init(hello_init);
module_exit(hello_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("Your Name");
MODULE_DESCRIPTION("Simple Linux kernel Hello World module");

