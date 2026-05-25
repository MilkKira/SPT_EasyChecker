using UnityEngine.Networking;

namespace SPT_EasyChecker_Client
{
    internal sealed class TrustAllCertificateHandler : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData)
        {
            return true;
        }
    }
}
