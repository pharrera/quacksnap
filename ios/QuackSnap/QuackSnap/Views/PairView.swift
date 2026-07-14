import SwiftUI

struct PairView: View {
    @Environment(AppModel.self) private var model
    @State private var code = ""
    @State private var isPairing = false
    @State private var error: String?
    @State private var showScanner = false

    var body: some View {
        VStack(spacing: 24) {
            Spacer()

            Image(systemName: "rectangle.on.rectangle.angled")
                .font(.system(size: 56))
                .foregroundStyle(.tint)

            Text("Pair with your computer")
                .font(.title2.weight(.semibold))

            Text("In QuackSnap on Windows, choose **Pair a device → Pair application…**, then scan the QR code or paste the pairing code.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .padding(.horizontal)

            Button {
                showScanner = true
            } label: {
                Label("Scan QR code", systemImage: "qrcode.viewfinder")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .padding(.horizontal)

            HStack {
                TextField("quacksnap://pair?…", text: $code)
                    .textFieldStyle(.roundedBorder)
                    .autocorrectionDisabled()
                    .textInputAutocapitalization(.never)
                Button("Pair") {
                    pair(with: code)
                }
                .buttonStyle(.bordered)
                .disabled(code.isEmpty || isPairing)
            }
            .padding(.horizontal)

            if isPairing {
                ProgressView("Pairing…")
            }
            if let error {
                Text(error)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal)
            }

            Spacer()
            Spacer()
        }
        .sheet(isPresented: $showScanner) {
            QRScannerView { scanned in
                showScanner = false
                pair(with: scanned)
            }
        }
    }

    private func pair(with uri: String) {
        error = nil
        isPairing = true
        Task {
            do {
                try await model.pair(with: uri)
            } catch {
                self.error = error.localizedDescription
            }
            isPairing = false
        }
    }
}
