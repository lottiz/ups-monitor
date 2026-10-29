using System.Runtime.CompilerServices;

// Il parsing delle risposte NUT è internal: non fa parte dell'API pubblica, ma va testato
// direttamente perché un errore di interpretazione dei valori falserebbe ogni soglia.
[assembly: InternalsVisibleTo("UPSControlCenter.Tests")]
