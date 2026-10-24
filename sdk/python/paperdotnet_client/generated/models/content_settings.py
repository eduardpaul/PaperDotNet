from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class ContentSettings(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The maxVersions property
    max_versions: Optional[int] = 50
    # The versioning property
    versioning: Optional[str] = "off"
    # The publishingEnabled property
    publishing_enabled: Optional[bool] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ContentSettings:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ContentSettings
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ContentSettings()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "maxVersions": lambda n : setattr(self, 'max_versions', n.get_int_value()),
            "publishingEnabled": lambda n : setattr(self, 'publishing_enabled', n.get_bool_value()),
            "versioning": lambda n : setattr(self, 'versioning', n.get_str_value()),
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
        writer.write_int_value("maxVersions", self.max_versions)
        writer.write_bool_value("publishingEnabled", self.publishing_enabled)
        writer.write_str_value("versioning", self.versioning)
        writer.write_additional_data_value(self.additional_data)


