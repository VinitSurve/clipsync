import Foundation
import CryptoKit
import CommonCrypto

public final class ClipSyncCrypto {
    private let symmetricKey: SymmetricKey

    public init(passphrase: String, salt: String) throws {
        guard let passData = passphrase.data(using: .utf8),
              let saltData = salt.data(using: .utf8) else {
            throw CryptoError.invalidInput
        }

        var derivedBytes = [UInt8](repeating: 0, count: 32)
        let status = passData.withUnsafeBytes { passBytes in
            saltData.withUnsafeBytes { sBytes in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    passBytes.baseAddress?.assumingMemoryBound(to: Int8.self),
                    passData.count,
                    sBytes.baseAddress?.assumingMemoryBound(to: UInt8.self),
                    saltData.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                    100000,
                    &derivedBytes,
                    32
                )
            }
        }

        guard status == kCCSuccess else {
            throw CryptoError.keyDerivationFailed(status)
        }

        self.symmetricKey = SymmetricKey(data: derivedBytes)
    }

    public func encrypt(plainText: String) throws -> EncryptedPayloadEnvelope {
        guard let data = plainText.data(using: .utf8) else {
            throw CryptoError.invalidInput
        }

        let nonce = AES.GCM.Nonce()
        let sealedBox = try AES.GCM.seal(data, using: symmetricKey, nonce: nonce)

        let ivBase64 = Self.toBase64URL(Data(nonce))
        let ciphertextBase64 = Self.toBase64URL(sealedBox.ciphertext)
        let tagBase64 = Self.toBase64URL(sealedBox.tag)

        return EncryptedPayloadEnvelope(
            version: 1,
            iv: ivBase64,
            ciphertext: ciphertextBase64,
            tag: tagBase64
        )
    }

    public func decrypt(envelope: EncryptedPayloadEnvelope) throws -> String {
        guard let ivData = Self.fromBase64URL(envelope.iv),
              let ciphertextData = Self.fromBase64URL(envelope.ciphertext),
              let tagData = Self.fromBase64URL(envelope.tag) else {
            throw CryptoError.invalidBase64
        }

        let nonce = try AES.GCM.Nonce(data: ivData)
        let sealedBox = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertextData, tag: tagData)
        let decryptedData = try AES.GCM.open(sealedBox, using: symmetricKey)

        guard let text = String(data: decryptedData, encoding: .utf8) else {
            throw CryptoError.utf8DecodingFailed
        }
        return text
    }

    public static func sha256Hex(_ string: String) -> String {
        guard let data = string.data(using: .utf8) else { return "" }
        let digest = SHA256.hash(data: data)
        return digest.compactMap { String(format: "%02x", $0) }.joined()
    }

    // Base64URL conversions
    public static func toBase64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    public static func fromBase64URL(_ string: String) -> Data? {
        var base64 = string
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        while base64.count % 4 != 0 {
            base64.append("=")
        }
        return Data(base64Encoded: base64)
    }

    public enum CryptoError: Error {
        case invalidInput
        case keyDerivationFailed(Int32)
        case invalidBase64
        case utf8DecodingFailed
    }
}
