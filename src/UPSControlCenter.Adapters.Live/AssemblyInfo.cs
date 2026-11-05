using System.Runtime.CompilerServices;

// Il parsing del MAC e la costruzione del magic packet sono internal: non fanno parte
// dell'API pubblica, ma vanno testati direttamente perché un errore nel formato del pacchetto
// significherebbe un Wake-on-LAN che non funziona mai, silenziosamente.
[assembly: InternalsVisibleTo("UPSControlCenter.Tests")]
