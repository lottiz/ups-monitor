using System.Runtime.CompilerServices;

// Il raggruppamento degli step per esecuzione è internal: non fa parte dell'API pubblica del
// motore, ma va testato direttamente perché un errore nell'ordinamento altera l'ordine di
// spegnimento/accensione dell'infrastruttura.
[assembly: InternalsVisibleTo("UPSControlCenter.Tests")]
