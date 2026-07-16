import SwiftUI

struct PairView: View {
    @Environment(AppModel.self) private var model
    @State private var code = ""
    @State private var uri = ""
    @State private var isPairing = false
    @State private var error: String?
    @State private var showScanner = false
    @State private var showAdvanced = false

    var body: some View {
        ScrollView {
            VStack(spacing: 22) {
                VStack(spacing: 16) {
                    BrandMark(size: 84)
                        .padding(.top, 28)

                    Text("Pair with your computer")
                        .font(.title2.bold())

                    Text("Open QuackSnap on Windows, choose **Pair a device**, and enter the 6-digit code it shows.")
                        .font(.subheadline)
                        .multilineTextAlignment(.center)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 28)
                }

                CodeField(code: $code) { pairWithCode() }
                    .padding(.top, 4)

                Group {
                    if isPairing {
                        HStack(spacing: 10) {
                            ProgressView()
                            Text("Looking for your computer…")
                                .font(.subheadline)
                                .foregroundStyle(.secondary)
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 8)
                    } else {
                        Button("Connect") { pairWithCode() }
                            .buttonStyle(PrimaryButtonStyle())
                            .disabled(code.count != 6)
                    }
                }
                .padding(.horizontal, 24)

                if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.footnote)
                        .foregroundStyle(Theme.coral)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 28)
                }

                otherWays
                    .padding(.top, 8)

                Spacer(minLength: 32)
            }
            .frame(maxWidth: .infinity)
        }
        .background(backdrop)
        .scrollDismissesKeyboard(.interactively)
        .sheet(isPresented: $showScanner) {
            QRScannerView { scanned in
                showScanner = false
                pair { try await model.pair(with: scanned) }
            }
        }
    }

    private var backdrop: some View {
        Theme.softGradient
            .ignoresSafeArea()
            .overlay(alignment: .top) {
                Theme.brandGradient
                    .frame(height: 260)
                    .blur(radius: 90)
                    .opacity(0.18)
                    .ignoresSafeArea()
            }
    }

    private var otherWays: some View {
        VStack(spacing: 12) {
            Button {
                withAnimation(.snappy) { showAdvanced.toggle() }
            } label: {
                HStack(spacing: 6) {
                    Text("Other ways to pair")
                    Image(systemName: "chevron.down")
                        .font(.caption.weight(.semibold))
                        .rotationEffect(.degrees(showAdvanced ? 180 : 0))
                }
                .font(.subheadline)
                .foregroundStyle(Theme.brand)
            }

            if showAdvanced {
                VStack(spacing: 12) {
                    Button {
                        showScanner = true
                    } label: {
                        Label("Scan QR code", systemImage: "qrcode.viewfinder")
                    }
                    .buttonStyle(SoftButtonStyle())

                    HStack(spacing: 8) {
                        TextField("quacksnap://pair?…", text: $uri)
                            .textFieldStyle(.roundedBorder)
                            .autocorrectionDisabled()
                            .textInputAutocapitalization(.never)
                        Button("Pair") {
                            pair { try await model.pair(with: uri) }
                        }
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Theme.brand)
                        .disabled(uri.isEmpty || isPairing)
                    }
                }
                .padding(16)
                .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                .transition(.opacity.combined(with: .move(edge: .top)))
            }
        }
        .padding(.horizontal, 24)
    }

    private func pairWithCode() {
        guard code.count == 6, !isPairing else { return }
        pair { try await model.pair(withCode: code) }
    }

    private func pair(_ action: @escaping () async throws -> Void) {
        error = nil
        isPairing = true
        Task {
            do {
                try await action()
            } catch {
                self.error = error.localizedDescription
                code = ""
            }
            isPairing = false
        }
    }
}
