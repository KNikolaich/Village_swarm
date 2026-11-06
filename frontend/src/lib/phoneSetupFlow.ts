// The phone way of setting a panel up (a QR code for its own Wi-Fi point) must not depend on the USB way working: it asks for
// its own token, when none is issued yet, and creates the monitor first when it is not in the hive yet.

export type PhoneSetupSteps = {
  /** Makes sure the monitor is in the hive (creates it when it is not). Rejects when it cannot be. */
  ensureMonitor: () => Promise<void>
  /** A new screen token for it: the old one stops working at once. */
  issueToken: () => Promise<string>
}

/** The monitor first, then its token; no token is issued for a monitor that could not be made. */
export async function preparePhoneSetup(steps: PhoneSetupSteps): Promise<string> {
  await steps.ensureMonitor()
  return steps.issueToken()
}
