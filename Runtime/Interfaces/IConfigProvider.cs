using System.Threading;
using System.Threading.Tasks;

namespace Flock.Interfaces
{
    // Config access is codegen-only: the generated accessors call GetByConfigIdAsync<T>.
    // The raw schema/patch getters are internal on FlockConfigProvider.
    public interface IConfigProvider
    {
        Task<T> GetByConfigIdAsync<T>(string configId, CancellationToken cancellationToken = default);
    }
}
