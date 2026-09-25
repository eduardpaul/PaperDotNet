from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class PreferencesPatch(AdditionalDataHolder, Parsable):
    """
    PATCH body (JSON merge patch): set a value, or null to inherit it again.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The dateFormat property
    date_format: Optional[str] = None
    # The documentLanguages property
    document_languages: Optional[str] = None
    # The language property
    language: Optional[str] = None
    # The numberFormat property
    number_format: Optional[str] = None
    # The theme property
    theme: Optional[str] = None
    # The timeFormat property
    time_format: Optional[str] = None
    # The timeZone property
    time_zone: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> PreferencesPatch:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: PreferencesPatch
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return PreferencesPatch()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "dateFormat": lambda n : setattr(self, 'date_format', n.get_str_value()),
            "documentLanguages": lambda n : setattr(self, 'document_languages', n.get_str_value()),
            "language": lambda n : setattr(self, 'language', n.get_str_value()),
            "numberFormat": lambda n : setattr(self, 'number_format', n.get_str_value()),
            "theme": lambda n : setattr(self, 'theme', n.get_str_value()),
            "timeFormat": lambda n : setattr(self, 'time_format', n.get_str_value()),
            "timeZone": lambda n : setattr(self, 'time_zone', n.get_str_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_str_value("dateFormat", self.date_format)
        writer.write_str_value("documentLanguages", self.document_languages)
        writer.write_str_value("language", self.language)
        writer.write_str_value("numberFormat", self.number_format)
        writer.write_str_value("theme", self.theme)
        writer.write_str_value("timeFormat", self.time_format)
        writer.write_str_value("timeZone", self.time_zone)
        writer.write_additional_data_value(self.additional_data)
    

