// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "QuackSnapKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "QuackSnapKit", targets: ["QuackSnapKit"]),
        .executable(name: "quacksnap-cli", targets: ["quacksnap-cli"]),
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-certificates.git", from: "1.0.0"),
        .package(url: "https://github.com/apple/swift-crypto.git", from: "3.0.0"),
    ],
    targets: [
        .target(
            name: "QuackSnapKit",
            dependencies: [
                .product(name: "X509", package: "swift-certificates"),
                .product(name: "Crypto", package: "swift-crypto"),
            ]
        ),
        .executableTarget(
            name: "quacksnap-cli",
            dependencies: ["QuackSnapKit"]
        ),
    ]
)
