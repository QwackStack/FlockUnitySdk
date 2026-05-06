// Native Keychain shim for IosTokenStore. Unity restricts this file to iOS
// builds automatically because it lives under Plugins/iOS — non-iOS targets
// never see it. Stores tokens as kSecClassGenericPassword with
// kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly so the OS handles
// encryption, the data survives backgrounding, and items never sync to iCloud.

#import <Foundation/Foundation.h>
#import <Security/Security.h>
#include <stdlib.h>
#include <string.h>

static NSMutableDictionary *FlockKeychainQuery(const char *service, const char *account) {
    NSMutableDictionary *q = [NSMutableDictionary dictionary];
    q[(__bridge id)kSecClass] = (__bridge id)kSecClassGenericPassword;
    q[(__bridge id)kSecAttrService] = [NSString stringWithUTF8String:service];
    q[(__bridge id)kSecAttrAccount] = [NSString stringWithUTF8String:account];
    return q;
}

extern "C" {

int FlockKeychainSet(const char *service, const char *account, const char *value) {
    NSMutableDictionary *q = FlockKeychainQuery(service, account);
    SecItemDelete((__bridge CFDictionaryRef)q);
    q[(__bridge id)kSecValueData] = [[NSString stringWithUTF8String:value] dataUsingEncoding:NSUTF8StringEncoding];
    q[(__bridge id)kSecAttrAccessible] = (__bridge id)kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly;
    return (int)SecItemAdd((__bridge CFDictionaryRef)q, NULL);
}

const char *FlockKeychainGet(const char *service, const char *account) {
    NSMutableDictionary *q = FlockKeychainQuery(service, account);
    q[(__bridge id)kSecReturnData] = (__bridge id)kCFBooleanTrue;
    q[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;
    CFTypeRef result = NULL;
    if (SecItemCopyMatching((__bridge CFDictionaryRef)q, &result) != errSecSuccess || result == NULL) return NULL;
    NSData *data = (__bridge_transfer NSData *)result;
    NSString *str = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
    return str ? strdup([str UTF8String]) : NULL;
}

int FlockKeychainDelete(const char *service, const char *account) {
    return (int)SecItemDelete((__bridge CFDictionaryRef)FlockKeychainQuery(service, account));
}

void FlockKeychainFreeString(const char *ptr) {
    if (ptr) free((void *)ptr);
}

}
